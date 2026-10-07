using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public static class GatewayHandler
{
    private const int MaxAuthProbeBytes = 64 * 1024;

    public static async Task HandleAsync(
        HttpContext context,
        string? path,
        IHttpClientFactory clients,
        IArcGisCredentialProvider credentials,
        IOptions<GatewayOptions> options,
        IOptions<ProtectionOptions> protectionOptions,
        IApplicationIdentityResolver applications,
        IArcGisResourceResolver resources,
        IArcGisOperationPolicy operationPolicy,
        IRequestActivityClassifier activityClassifier,
        IUserActivityRateLimiter userRateLimiter,
        IUserConcurrencyGate userConcurrencyGate,
        IArcGisUpstreamGate upstreamGate,
        IUserBlockStore userBlocks,
        IAccessPolicyClient accessPolicy,
        ITelemetryQueue telemetry,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ArcGisAudit");
        var cfg = options.Value;
        var protection = protectionOptions.Value;
        var subject = context.User.FindFirstValue("sub") ?? "unknown";
        var tenant = context.User.FindFirstValue("tenant_id") ??
                     context.User.FindFirstValue("tid");
        var cid = context.TraceIdentifier;

        if ((path?.Length ?? 0) > protection.MaxRequestPathLength ||
            context.Request.QueryString.Value?.Length > protection.MaxQueryStringLength)
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                "unknown",
                null,
                StatusCodes.Status414UriTooLong,
                "request_uri_too_long",
                cid,
                path ?? "unknown");
            return;
        }

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
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                "unknown",
                null,
                StatusCodes.Status503ServiceUnavailable,
                "block_store_unavailable",
                cid,
                "blocked-check");
            return;
        }

        if (activeBlock is not null)
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                "unknown",
                null,
                StatusCodes.Status403Forbidden,
                "user_blocked",
                cid,
                "blocked");
            logger.LogWarning(
                "arcgis_user_blocked earthid_sub={EarthIdSub} reason={Reason} expires_at={ExpiresAt} correlation_id={CorrelationId}",
                subject,
                activeBlock.Reason,
                activeBlock.ExpiresAt,
                cid);
            return;
        }

        if (!applications.TryResolve(context.User, out var application))
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                "unknown",
                null,
                StatusCodes.Status403Forbidden,
                "application_denied",
                cid,
                path ?? "unknown");
            return;
        }

        if (!resources.TryResolve(path, out var resource))
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                null,
                StatusCodes.Status403Forbidden,
                "resource_invalid",
                cid,
                path ?? "unknown");
            return;
        }

        var normalized = resource.CanonicalPath;
        if (!cfg.AllowedPathPrefixes.Any(
                prefix => IsWithinPrefix(normalized, prefix)))
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                resource,
                StatusCodes.Status403Forbidden,
                "path_denied",
                cid,
                normalized);
            return;
        }

        if (!operationPolicy.IsAllowed(resource, context.Request.Method))
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                resource,
                StatusCodes.Status403Forbidden,
                "operation_denied",
                cid,
                normalized);
            return;
        }

        var postBodyResult = await ReadPostBodyAsync(
            context,
            protection.MaxRequestBodyBytes);
        if (postBodyResult.TooLarge)
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                resource,
                StatusCodes.Status413PayloadTooLarge,
                "request_body_too_large",
                cid,
                normalized);
            return;
        }

        var postBody = postBodyResult.Body;
        if (ContainsClientArcGisCredential(context.Request, postBody))
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                resource,
                StatusCodes.Status400BadRequest,
                "client_arcgis_credential_forbidden",
                cid,
                normalized);
            return;
        }

        var activity = activityClassifier.Classify(
            resource,
            context.Request.Query,
            postBody,
            context.Request.ContentType);

        var rateDecision = await userRateLimiter.ConsumeAsync(
            new UserActivityRateKey(
                subject,
                tenant,
                application.Id,
                resource.ServiceName,
                resource.LayerId,
                resource.Operation),
            activity,
            context.RequestAborted);

        if (!rateDecision.Allowed)
        {
            var backendUnavailable = rateDecision.ReasonCode is
                "rate_backend_unavailable" or "rate_backend_malformed";
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
                activity: activity,
                rateDecision: rateDecision);

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
            subject,
            tenant,
            application.Id,
            resource.ServiceName,
            resource.ServiceType,
            resource.LayerId,
            resource.Operation,
            context.Request.Method,
            cid);

        var accessDecision = await accessPolicy.AuthorizeAsync(
            application,
            accessRequest,
            context.RequestAborted);
        if (!accessDecision.Allowed)
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                resource,
                StatusCodes.Status403Forbidden,
                accessDecision.ReasonCode,
                cid,
                normalized,
                accessDecision.PolicyVersion,
                activity,
                rateDecision);
            return;
        }

        if (!TryBuildTarget(
                cfg.ArcGisBaseUrl,
                normalized,
                context.Request.QueryString,
                out var target))
        {
            Deny(
                context,
                telemetry,
                logger,
                subject,
                tenant,
                application.Id,
                resource,
                StatusCodes.Status503ServiceUnavailable,
                "upstream_configuration_invalid",
                cid,
                normalized,
                accessDecision.PolicyVersion,
                activity,
                rateDecision);
            return;
        }

        IUserConcurrencyLease? userLease;
        try
        {
            userLease = await userConcurrencyGate.TryEnterAsync(
                new UserConcurrencyKey(subject, tenant, application.Id),
                activity.IsHeavy,
                context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application.Id,
                resource,
                context.Request.Method,
                StatusCodes.Status503ServiceUnavailable,
                0,
                "DENY",
                "user_concurrency_backend_unavailable",
                accessDecision.PolicyVersion,
                cid,
                activity: activity,
                rateDecision: rateDecision);
            logger.LogError(
                ex,
                "user_concurrency_backend_unavailable earthid_sub={EarthIdSub} correlation_id={CorrelationId}",
                subject,
                cid);
            return;
        }

        if (userLease is null)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "1";
            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application.Id,
                resource,
                context.Request.Method,
                StatusCodes.Status429TooManyRequests,
                0,
                "THROTTLE",
                "user_concurrency_exhausted",
                accessDecision.PolicyVersion,
                cid,
                activity: activity,
                rateDecision: rateDecision,
                concurrencyClass: activity.IsHeavy ? "heavy" : "interactive");
            return;
        }

        await using (userLease)
        {
            using var upstreamLease = await upstreamGate.TryEnterAsync(
                context.RequestAborted);
            if (upstreamLease is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "1";
                RecordTelemetry(
                    telemetry,
                    subject,
                    tenant,
                    application.Id,
                    resource,
                    context.Request.Method,
                    StatusCodes.Status503ServiceUnavailable,
                    0,
                    "THROTTLE",
                    "upstream_concurrency_exhausted",
                    accessDecision.PolicyVersion,
                    cid,
                    activity: activity,
                    rateDecision: rateDecision,
                    concurrencyClass: userLease.Class);
                Audit(
                    logger,
                    subject,
                    normalized,
                    context.Request.Method,
                    StatusCodes.Status503ServiceUnavailable,
                    0,
                    cid,
                    "upstream_concurrency_exhausted");
                return;
            }

            await ProxyToArcGisAsync(
                context,
                clients,
                credentials,
                target,
                postBody,
                subject,
                tenant,
                application.Id,
                resource,
                normalized,
                accessDecision,
                activity,
                rateDecision,
                userLease.Class,
                cid,
                telemetry,
                logger);
        }
    }

    private static async Task ProxyToArcGisAsync(
        HttpContext context,
        IHttpClientFactory clients,
        IArcGisCredentialProvider credentials,
        Uri target,
        byte[]? postBody,
        string subject,
        string? tenant,
        string application,
        ArcGisResource resource,
        string normalized,
        AccessPolicyDecision accessDecision,
        RequestActivity activity,
        UserRateDecision rateDecision,
        string concurrencyClass,
        string cid,
        ITelemetryQueue telemetry,
        ILogger logger)
    {
        var sw = Stopwatch.StartNew();
        var client = clients.CreateClient("arcgis");
        UpstreamResult? result = null;

        try
        {
            var token = await GetCredentialAsync(
                credentials,
                context.RequestAborted);
            result = await SendAsync(
                client,
                target,
                context,
                postBody,
                token,
                cid);

            if (IsAuthFailure(result))
            {
                result.Dispose();
                result = null;
                credentials.InvalidateToken();

                logger.LogWarning(
                    "arcgis_auth_retry earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                    subject,
                    normalized,
                    cid);

                token = await GetCredentialAsync(
                    credentials,
                    context.RequestAborted);
                result = await SendAsync(
                    client,
                    target,
                    context,
                    postBody,
                    token,
                    cid);
            }

            context.Response.StatusCode = result.Status;
            if (result.ContentType is not null)
                context.Response.ContentType = result.ContentType;

            result.ApplySafeResponseHeaders(context.Response);

            if (result.Prefix.Length > 0)
                await context.Response.Body.WriteAsync(
                    result.Prefix,
                    context.RequestAborted);

            if (!result.EndOfStream)
                await result.Stream.CopyToAsync(
                    context.Response.Body,
                    context.RequestAborted);

            sw.Stop();
            Audit(
                logger,
                subject,
                normalized,
                context.Request.Method,
                result.Status,
                sw.ElapsedMilliseconds,
                cid,
                $"allow:{application}:{resource.ServiceName}:{resource.LayerId}:{resource.Operation}");
            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application,
                resource,
                context.Request.Method,
                result.Status,
                sw.ElapsedMilliseconds,
                "ALLOW",
                accessDecision.ReasonCode,
                accessDecision.PolicyVersion,
                cid,
                responseBytes: result.ContentLength,
                activity: activity,
                rateDecision: rateDecision,
                concurrencyClass: concurrencyClass);
        }
        catch (CredentialProviderException ex)
        {
            sw.Stop();
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;

            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application,
                resource,
                context.Request.Method,
                context.Response.HasStarted
                    ? context.Response.StatusCode
                    : StatusCodes.Status502BadGateway,
                sw.ElapsedMilliseconds,
                "DENY",
                "credential_provider_failure",
                accessDecision.PolicyVersion,
                cid,
                activity: activity,
                rateDecision: rateDecision,
                concurrencyClass: concurrencyClass);
            logger.LogError(
                ex.InnerException ?? ex,
                "arcgis_credential_provider_failure earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject,
                normalized,
                cid);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            sw.Stop();
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;

            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application,
                resource,
                context.Request.Method,
                context.Response.HasStarted
                    ? context.Response.StatusCode
                    : StatusCodes.Status504GatewayTimeout,
                sw.ElapsedMilliseconds,
                "DENY",
                "upstream_timeout",
                accessDecision.PolicyVersion,
                cid,
                activity: activity,
                rateDecision: rateDecision,
                concurrencyClass: concurrencyClass);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;

            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application,
                resource,
                context.Request.Method,
                context.Response.HasStarted
                    ? context.Response.StatusCode
                    : StatusCodes.Status502BadGateway,
                sw.ElapsedMilliseconds,
                "DENY",
                "upstream_unavailable",
                accessDecision.PolicyVersion,
                cid,
                activity: activity,
                rateDecision: rateDecision,
                concurrencyClass: concurrencyClass);
            logger.LogWarning(
                ex,
                "arcgis_upstream_unavailable earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject,
                normalized,
                cid);
        }
        catch (IOException ex)
        {
            sw.Stop();
            var status = context.Response.HasStarted
                ? context.Response.StatusCode
                : StatusCodes.Status502BadGateway;

            if (context.Response.HasStarted)
                context.Abort();
            else
                context.Response.StatusCode = status;

            RecordTelemetry(
                telemetry,
                subject,
                tenant,
                application,
                resource,
                context.Request.Method,
                status,
                sw.ElapsedMilliseconds,
                "DENY",
                "upstream_stream_failure",
                accessDecision.PolicyVersion,
                cid,
                activity: activity,
                rateDecision: rateDecision,
                concurrencyClass: concurrencyClass);
            logger.LogWarning(
                ex,
                "arcgis_upstream_stream_failure earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject,
                normalized,
                cid);
        }
        finally
        {
            result?.Dispose();
        }
    }

    private static async Task<string> GetCredentialAsync(
        IArcGisCredentialProvider credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.GetTokenAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            throw new CredentialProviderException(ex);
        }
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadPostBodyAsync(
        HttpContext context,
        long maxRequestBodyBytes)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
            return (null, false);

        if (context.Request.ContentLength is > 0 &&
            context.Request.ContentLength > maxRequestBodyBytes)
        {
            return (null, true);
        }

        using var bufferStream = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            var remaining = maxRequestBodyBytes + 1 - bufferStream.Length;
            if (remaining <= 0)
                return (null, true);

            var read = await context.Request.Body.ReadAsync(
                buffer.AsMemory(
                    0,
                    (int)Math.Min(buffer.Length, remaining)),
                context.RequestAborted);

            if (read == 0)
                break;

            await bufferStream.WriteAsync(
                buffer.AsMemory(0, read),
                context.RequestAborted);
        }

        if (bufferStream.Length > maxRequestBodyBytes)
            return (null, true);

        return (bufferStream.ToArray(), false);
    }

    private static bool ContainsClientArcGisCredential(
        HttpRequest request,
        byte[]? postBody)
    {
        if (request.Headers.ContainsKey("X-Esri-Authorization"))
            return true;

        if (request.Query.Keys.Any(
                key => string.Equals(
                    key,
                    "token",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (postBody is null ||
            postBody.Length == 0 ||
            string.IsNullOrWhiteSpace(request.ContentType) ||
            !request.ContentType.StartsWith(
                "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var form = QueryHelpers.ParseQuery(
            "?" + Encoding.UTF8.GetString(postBody));
        return form.Keys.Any(
            key => string.Equals(
                key,
                "token",
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithinPrefix(string path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        var normalizedPrefix = prefix.TrimEnd('/');
        return path.Equals(
                   normalizedPrefix,
                   StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(
                   normalizedPrefix + "/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildTarget(
        string baseUrl,
        string normalizedPath,
        QueryString queryString,
        out Uri target)
    {
        target = null!;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment) ||
            baseUri.AbsolutePath != "/")
        {
            return false;
        }

        if (!Uri.TryCreate(
                baseUri,
                normalizedPath.TrimStart('/') + queryString.Value,
                out var candidate))
        {
            return false;
        }

        if (!string.Equals(
                candidate.Scheme,
                baseUri.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidate.Host,
                baseUri.Host,
                StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != baseUri.Port)
        {
            return false;
        }

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
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            target);
        request.Headers.TryAddWithoutValidation(
            "X-Esri-Authorization",
            "Bearer " + token);
        request.Headers.TryAddWithoutValidation(
            "X-Correlation-ID",
            cid);
        CopySafeRequestHeaders(context.Request, request);

        if (postBody is not null)
        {
            request.Content = new ByteArrayContent(postBody);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType) &&
                MediaTypeHeaderValue.TryParse(
                    context.Request.ContentType,
                    out var contentType))
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
            var stream = await response.Content.ReadAsStreamAsync(
                context.RequestAborted);
            var shouldProbe =
                (int)response.StatusCode is 401 or 498 or 499 ||
                response.Content.Headers.ContentType?.MediaType?.Contains(
                    "json",
                    StringComparison.OrdinalIgnoreCase) == true;

            if (!shouldProbe)
            {
                return new UpstreamResult(
                    response,
                    stream,
                    Array.Empty<byte>(),
                    false);
            }

            using var probe = new MemoryStream();
            var buffer = new byte[8192];

            while (probe.Length <= MaxAuthProbeBytes)
            {
                var remaining = MaxAuthProbeBytes + 1 -
                                checked((int)probe.Length);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(
                        0,
                        Math.Min(buffer.Length, remaining)),
                    context.RequestAborted);

                if (read == 0)
                {
                    return new UpstreamResult(
                        response,
                        stream,
                        probe.ToArray(),
                        true);
                }

                await probe.WriteAsync(
                    buffer.AsMemory(0, read),
                    context.RequestAborted);

                if (probe.Length > MaxAuthProbeBytes)
                    break;
            }

            return new UpstreamResult(
                response,
                stream,
                probe.ToArray(),
                false);
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
            {
                destination.Headers.TryAddWithoutValidation(
                    name,
                    values.ToArray());
            }
        }
    }

    private static bool IsAuthFailure(UpstreamResult result)
    {
        if (result.Status is 401 or 498 or 499)
            return true;

        if (!result.EndOfStream ||
            result.Prefix.Length == 0 ||
            result.Prefix.Length > MaxAuthProbeBytes)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Prefix);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var code))
            {
                return false;
            }

            return code.ValueKind switch
            {
                JsonValueKind.Number when code.TryGetInt32(out var n) =>
                    n is 498 or 499,
                JsonValueKind.String when int.TryParse(
                    code.GetString(),
                    out var n) => n is 498 or 499,
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
        string? policyVersion = null,
        RequestActivity? activity = null,
        UserRateDecision? rateDecision = null)
    {
        context.Response.StatusCode = status;
        Audit(
            logger,
            subject,
            path,
            context.Request.Method,
            status,
            0,
            cid,
            reason);
        RecordTelemetry(
            telemetry,
            subject,
            tenant,
            application,
            resource,
            context.Request.Method,
            status,
            0,
            "DENY",
            reason,
            policyVersion,
            cid,
            activity: activity,
            rateDecision: rateDecision);
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
        string reasonCode,
        string? policyVersion,
        string cid,
        long? responseBytes = null,
        RequestActivity? activity = null,
        UserRateDecision? rateDecision = null,
        string? concurrencyClass = null,
        long? recordCount = null)
    {
        telemetry.TryWrite(
            new TelemetryEvent(
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
                reasonCode,
                policyVersion,
                cid,
                responseBytes,
                rateDecision?.RemainingAggregateUnits,
                activity?.Class,
                activity?.CostUnits,
                rateDecision?.RemainingResourceUnits,
                rateDecision?.ProfileVersion,
                concurrencyClass,
                activity?.IsSpatial,
                activity?.ReturnsGeometry,
                activity?.IsPaged,
                activity?.IsHeavy,
                activity?.IsExtractionLike,
                activity?.QueryFingerprint,
                recordCount));
    }

    private static void Audit(
        ILogger logger,
        string subject,
        string path,
        string method,
        int status,
        long durationMs,
        string cid,
        string reason)
    {
        logger.LogInformation(
            "arcgis_gateway earthid_sub={EarthIdSub} path={Path} method={Method} status={Status} duration_ms={DurationMs} correlation_id={CorrelationId} reason={Reason}",
            subject,
            path,
            method,
            status,
            durationMs,
            cid,
            reason);
    }

    private sealed class CredentialProviderException(Exception inner)
        : Exception("ArcGIS credential provider failed.", inner);

    private sealed class UpstreamResult : IDisposable
    {
        private readonly HttpResponseMessage response;

        public UpstreamResult(
            HttpResponseMessage response,
            Stream stream,
            byte[] prefix,
            bool endOfStream)
        {
            this.response = response;
            Stream = stream;
            Prefix = prefix;
            EndOfStream = endOfStream;
        }

        public int Status => (int)response.StatusCode;
        public string? ContentType =>
            response.Content.Headers.ContentType?.ToString();
        public long? ContentLength =>
            response.Content.Headers.ContentLength;
        public Stream Stream { get; }
        public byte[] Prefix { get; }
        public bool EndOfStream { get; }

        public void ApplySafeResponseHeaders(HttpResponse destination)
        {
            CopyHeader(response.Headers, destination, "ETag");
            CopyHeader(response.Headers, destination, "Cache-Control");
            CopyHeader(response.Headers, destination, "Last-Modified");
            CopyHeader(response.Headers, destination, "Accept-Ranges");
            CopyHeader(response.Content.Headers, destination, "Content-Range");
            CopyHeader(response.Content.Headers, destination, "Content-Disposition");
            CopyHeader(response.Content.Headers, destination, "Expires");
        }

        public void Dispose()
        {
            Stream.Dispose();
            response.Dispose();
        }

        private static void CopyHeader(
            HttpHeaders source,
            HttpResponse destination,
            string name)
        {
            if (source.TryGetValues(name, out var values))
                destination.Headers[name] = values.ToArray();
        }
    }
}
