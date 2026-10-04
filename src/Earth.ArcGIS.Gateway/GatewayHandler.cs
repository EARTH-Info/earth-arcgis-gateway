using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public static class GatewayHandler
{
    private const long MaxRequestBodyBytes = 2 * 1024 * 1024;
    private const int MaxAuthProbeBytes = 64 * 1024;

    public static async Task HandleAsync(HttpContext context, string? path, IHttpClientFactory clients,
        IArcGisCredentialProvider credentials, IOptions<GatewayOptions> options, IApplicationIdentityResolver applications,
        IArcGisResourceResolver resources, IArcGisOperationPolicy operationPolicy, IRateCostPolicy rateCostPolicy,
        IGatewayRateLimiter gatewayRateLimiter, IArcGisUpstreamGate upstreamGate, IUserBlockStore userBlocks,
        IAccessPolicyClient accessPolicy, ITelemetryQueue telemetry, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ArcGisAudit");
        var cfg = options.Value;
        var subject = context.User.FindFirstValue("sub") ?? "unknown";
        var tenant = context.User.FindFirstValue("tenant_id") ?? context.User.FindFirstValue("tid");
        var cid = context.TraceIdentifier;

        UserBlock? activeBlock;
        try
        {
            activeBlock = await userBlocks.GetActiveAsync(
                subject,
                context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "user_block_store_unavailable earthid_sub={EarthIdSub} correlation_id={CorrelationId}",
                subject,
                cid);
            Deny(context, telemetry, logger, subject, tenant, "unknown", null,
                StatusCodes.Status503ServiceUnavailable,
                "block_store_unavailable",
                cid,
                "blocked-check");
            return;
        }

        if (activeBlock is not null)
        {
            Deny(context, telemetry, logger, subject, tenant, "unknown", null,
                StatusCodes.Status403Forbidden, "user_blocked", cid, "blocked");
            logger.LogWarning(
                "arcgis_user_blocked earthid_sub={EarthIdSub} reason={Reason} expires_at={ExpiresAt} correlation_id={CorrelationId}",
                subject, activeBlock.Reason, activeBlock.ExpiresAt, cid);
            return;
        }

        if (!applications.TryResolve(context.User, out var application))
        {
            Deny(context, telemetry, logger, subject, tenant, "unknown", null,
                StatusCodes.Status403Forbidden, "application_denied", cid, path ?? "unknown");
            return;
        }

        if (!resources.TryResolve(path, out var resource))
        {
            Deny(context, telemetry, logger, subject, tenant, application.Id, null,
                StatusCodes.Status403Forbidden, "resource_invalid", cid, path ?? "unknown");
            return;
        }

        var normalized = resource.CanonicalPath;
        if (!cfg.AllowedPathPrefixes.Any(prefix => IsWithinPrefix(normalized, prefix)))
        {
            Deny(context, telemetry, logger, subject, tenant, application.Id, resource,
                StatusCodes.Status403Forbidden, "path_denied", cid, normalized);
            return;
        }

        if (!operationPolicy.IsAllowed(resource, context.Request.Method))
        {
            Deny(context, telemetry, logger, subject, tenant, application.Id, resource,
                StatusCodes.Status403Forbidden, "operation_denied", cid, normalized);
            return;
        }

        var postBodyResult = await ReadPostBodyAsync(context);
        if (postBodyResult.TooLarge)
        {
            Deny(context, telemetry, logger, subject, tenant, application.Id, resource,
                StatusCodes.Status413PayloadTooLarge, "request_body_too_large", cid, normalized);
            return;
        }

        var postBody = postBodyResult.Body;

        var rateCost = rateCostPolicy.GetCost(
            resource,
            context.Request.Query,
            postBody,
            context.Request.ContentType);
        var rateDecision = await gatewayRateLimiter.ConsumeAsync(
            new RateLimitKey(subject, tenant, application.Id, resource.ServiceName, resource.LayerId, resource.Operation),
            rateCost.Units,
            context.RequestAborted);

        if (!rateDecision.Allowed)
        {
            var backendUnavailable =
                rateDecision.ReasonCode is
                    "rate_backend_unavailable" or
                    "rate_backend_malformed";

            var status = backendUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status429TooManyRequests;

            context.Response.StatusCode = status;
            context.Response.Headers.RetryAfter =
                Math.Max(1, rateDecision.RetryAfterSeconds).ToString();

            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application.Id,
                resource,
                context.Request.Method,
                status,
                0,
                backendUnavailable ? "DENY" : "THROTTLE",
                rateDecision.ReasonCode,
                null,
                cid,
                rateLimitRemaining: rateDecision.RemainingUnits);

            Audit(
                logger,
                subject,
                normalized,
                context.Request.Method,
                status,
                0,
                cid,
                rateDecision.ReasonCode);
            return;
        }

        var accessRequest = new AccessPolicyRequest(
            subject, tenant, application.Id, resource.ServiceName, resource.ServiceType,
            resource.LayerId, resource.Operation, context.Request.Method, cid);

        var accessDecision = await accessPolicy.AuthorizeAsync(application, accessRequest, context.RequestAborted);
        if (!accessDecision.Allowed)
        {
            Deny(context, telemetry, logger, subject, tenant, application.Id, resource,
                StatusCodes.Status403Forbidden, accessDecision.ReasonCode, cid, normalized,
                accessDecision.PolicyVersion);
            return;
        }

        if (!TryBuildTarget(cfg.ArcGisBaseUrl, normalized, context.Request.QueryString, out var target))
        {
            Deny(context, telemetry, logger, subject, tenant, application.Id, resource,
                StatusCodes.Status503ServiceUnavailable, "upstream_configuration_invalid", cid, normalized,
                accessDecision.PolicyVersion);
            return;
        }

        using var upstreamLease = await upstreamGate.TryEnterAsync(context.RequestAborted);
        if (upstreamLease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            RecordTelemetry(telemetry, subject, tenant, application.Id, resource,
                context.Request.Method, StatusCodes.Status503ServiceUnavailable, 0,
                "THROTTLE", "upstream_concurrency_exhausted", accessDecision.PolicyVersion, cid);
            Audit(logger, subject, normalized, context.Request.Method,
                StatusCodes.Status503ServiceUnavailable, 0, cid, "upstream_concurrency_exhausted");
            return;
        }

        var sw = Stopwatch.StartNew();
        var client = clients.CreateClient("arcgis");
        UpstreamResult? result = null;

        try
        {
            var token = await credentials.GetTokenAsync(context.RequestAborted);
            result = await SendAsync(client, target, context, postBody, token, cid);

            if (IsAuthFailure(result))
            {
                result.Dispose();
                result = null;
                credentials.InvalidateToken();

                logger.LogWarning(
                    "arcgis_auth_retry earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                    subject, normalized, cid);

                token = await credentials.GetTokenAsync(context.RequestAborted);
                result = await SendAsync(client, target, context, postBody, token, cid);
            }

            context.Response.StatusCode = result.Status;
            if (result.ContentType is not null)
                context.Response.ContentType = result.ContentType;

            result.ApplySafeResponseHeaders(context.Response);

            if (result.Prefix.Length > 0)
                await context.Response.Body.WriteAsync(result.Prefix, context.RequestAborted);

            if (!result.EndOfStream)
                await result.Stream.CopyToAsync(context.Response.Body, context.RequestAborted);

            sw.Stop();
            Audit(logger, subject, normalized, context.Request.Method, result.Status,
                sw.ElapsedMilliseconds, cid,
                $"allow:{application.Id}:{resource.ServiceName}:{resource.LayerId}:{resource.Operation}");
            RecordTelemetry(telemetry, subject, tenant, application.Id, resource,
                context.Request.Method, result.Status, sw.ElapsedMilliseconds, "ALLOW",
                accessDecision.ReasonCode, accessDecision.PolicyVersion, cid,
                responseBytes: result.ContentLength,
                rateLimitRemaining: rateDecision.RemainingUnits);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            sw.Stop();
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;

            RecordTelemetry(telemetry, subject, tenant, application.Id, resource,
                context.Request.Method, StatusCodes.Status504GatewayTimeout, sw.ElapsedMilliseconds,
                "DENY", "upstream_timeout", accessDecision.PolicyVersion, cid);
            logger.LogWarning("arcgis_upstream_timeout earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject, normalized, cid);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;

            RecordTelemetry(telemetry, subject, tenant, application.Id, resource,
                context.Request.Method, StatusCodes.Status502BadGateway, sw.ElapsedMilliseconds,
                "DENY", "upstream_unavailable", accessDecision.PolicyVersion, cid);
            logger.LogWarning(ex,
                "arcgis_upstream_unavailable earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject, normalized, cid);
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;

            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application.Id,
                resource,
                context.Request.Method,
                StatusCodes.Status502BadGateway,
                sw.ElapsedMilliseconds,
                "DENY",
                "credential_provider_failure",
                accessDecision.PolicyVersion,
                cid);

            logger.LogError(
                ex,
                "arcgis_credential_provider_failure earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject,
                normalized,
                cid);
        }
        finally
        {
            result?.Dispose();
        }
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadPostBodyAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
            return (null, false);

        if (context.Request.ContentLength is > MaxRequestBodyBytes)
            return (null, true);

        using var bufferStream = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            var remaining = MaxRequestBodyBytes + 1 - bufferStream.Length;
            if (remaining <= 0)
                return (null, true);

            var read = await context.Request.Body.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                context.RequestAborted);

            if (read == 0)
                break;

            await bufferStream.WriteAsync(
                buffer.AsMemory(0, read),
                context.RequestAborted);
        }

        if (bufferStream.Length > MaxRequestBodyBytes)
            return (null, true);

        return (bufferStream.ToArray(), false);
    }

    private static bool IsWithinPrefix(string path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        var normalizedPrefix = prefix.TrimEnd('/');
        return path.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildTarget(string baseUrl, string normalizedPath, QueryString queryString, out Uri target)
    {
        target = null!;

        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (!Uri.TryCreate(baseUri, normalizedPath.TrimStart('/') + queryString.Value, out var candidate))
            return false;

        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != baseUri.Port)
            return false;

        target = candidate;
        return true;
    }

    private static async Task<UpstreamResult> SendAsync(
        HttpClient client,
        Uri target,
        HttpContext context,
        byte[]? postBody,
        string token,
        string cid)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        request.Headers.TryAddWithoutValidation("X-Esri-Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", cid);
        CopySafeRequestHeaders(context.Request, request);

        if (postBody is not null)
        {
            request.Content = new ByteArrayContent(postBody);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType) &&
                MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
            {
                request.Content.Headers.ContentType = contentType;
            }
        }

        var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var shouldProbe = (int)response.StatusCode is 401 or 498 or 499 ||
                              response.Content.Headers.ContentType?.MediaType?.Contains(
                                  "json", StringComparison.OrdinalIgnoreCase) == true;

            if (!shouldProbe)
                return new UpstreamResult(response, stream, Array.Empty<byte>(), false);

            using var probe = new MemoryStream();
            var buffer = new byte[8192];

            while (probe.Length <= MaxAuthProbeBytes)
            {
                var remaining = MaxAuthProbeBytes + 1 - checked((int)probe.Length);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    context.RequestAborted);

                if (read == 0)
                    return new UpstreamResult(response, stream, probe.ToArray(), true);

                await probe.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);

                if (probe.Length > MaxAuthProbeBytes)
                    break;
            }

            return new UpstreamResult(response, stream, probe.ToArray(), false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }


    private static void CopySafeRequestHeaders(
        HttpRequest source,
        HttpRequestMessage destination)
    {
        foreach (var name in new[]
                 {
                     "Accept",
                     "If-None-Match",
                     "If-Modified-Since",
                     "Range"
                 })
        {
            if (source.Headers.TryGetValue(name, out var values))
                destination.Headers.TryAddWithoutValidation(
                    name,
                    values.ToArray());
        }
    }

    private static bool IsAuthFailure(UpstreamResult result)
    {
        if (result.Status is 401 or 498 or 499)
            return true;

        if (!result.EndOfStream || result.Prefix.Length == 0 || result.Prefix.Length > MaxAuthProbeBytes)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(result.Prefix);
            if (!doc.RootElement.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("code", out var code))
                return false;

            return code.ValueKind switch
            {
                JsonValueKind.Number when code.TryGetInt32(out var n) => n is 498 or 499,
                JsonValueKind.String when int.TryParse(code.GetString(), out var n) => n is 498 or 499,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Deny(
        HttpContext context,
        ITelemetryQueue telemetry,
        ILogger logger,
        string subject,
        string? tenant,
        string application,
        ArcGisResource? resource,
        int status,
        string reason,
        string cid,
        string path,
        string? policyVersion = null)
    {
        context.Response.StatusCode = status;
        Audit(logger, subject, path, context.Request.Method, status, 0, cid, reason);
        RecordTelemetry(telemetry, subject, tenant, application, resource,
            context.Request.Method, status, 0, "DENY", reason, policyVersion, cid);
    }

    private static void RecordTelemetry(
        ITelemetryQueue telemetry,
        string subject,
        string? tenant,
        string application,
        ArcGisResource? resource,
        string method,
        int status,
        long durationMs,
        string decision,
        string reason,
        string? policyVersion,
        string cid,
        long? responseBytes = null,
        int? rateLimitRemaining = null)
    {
        telemetry.TryWrite(new TelemetryEvent(
            DateTimeOffset.UtcNow,
            subject,
            tenant,
            application,
            resource?.ServiceName ?? "unknown",
            resource?.ServiceType ?? "unknown",
            resource?.LayerId,
            resource?.Operation ?? "unknown",
            method,
            status,
            durationMs,
            decision,
            reason,
            policyVersion,
            cid,
            responseBytes,
            rateLimitRemaining));
    }

    private static void Audit(
        ILogger logger,
        string sub,
        string path,
        string method,
        int status,
        long ms,
        string cid,
        string decision) =>
        logger.LogInformation(
            "arcgis_request earthid_sub={EarthIdSub} method={Method} path={Path} status={Status} duration_ms={DurationMs} decision={Decision} correlation_id={CorrelationId}",
            sub, method, path, status, ms, decision, cid);

    private sealed class UpstreamResult : IDisposable
    {
        private readonly HttpResponseMessage _response;

        public UpstreamResult(HttpResponseMessage response, Stream stream, byte[] prefix, bool endOfStream)
        {
            _response = response;
            Stream = stream;
            Prefix = prefix;
            EndOfStream = endOfStream;
        }

        public int Status => (int)_response.StatusCode;
        public string? ContentType => _response.Content.Headers.ContentType?.ToString();
        public long? ContentLength => _response.Content.Headers.ContentLength;
        public Stream Stream { get; }
        public byte[] Prefix { get; }
        public bool EndOfStream { get; }

        public void ApplySafeResponseHeaders(HttpResponse response)
        {
            CopyHeader(_response.Headers, response, "Cache-Control");
            CopyHeader(_response.Headers, response, "ETag");
            CopyHeader(_response.Headers, response, "Vary");
            CopyHeader(_response.Headers, response, "Accept-Ranges");
            CopyHeader(_response.Content.Headers, response, "Last-Modified");
            CopyHeader(_response.Content.Headers, response, "Expires");
            CopyHeader(_response.Content.Headers, response, "Content-Disposition");
        }

        private static void CopyHeader(
            HttpHeaders source,
            HttpResponse destination,
            string name)
        {
            if (source.TryGetValues(name, out var values))
                destination.Headers[name] = values.ToArray();
        }

        public void Dispose() => _response.Dispose();
    }
}
