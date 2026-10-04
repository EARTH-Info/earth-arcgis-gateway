using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public static class GatewayHandler
{
    public static async Task HandleAsync(HttpContext context, string? path, IHttpClientFactory clients,
        ArcGisTokenProvider tokens, IOptions<GatewayOptions> options, IApplicationIdentityResolver applications,
        IArcGisResourceResolver resources, IArcGisOperationPolicy operationPolicy, IRateCostPolicy rateCostPolicy,
        IGatewayRateLimiter gatewayRateLimiter, IAccessPolicyClient accessPolicy,
        ITelemetryQueue telemetry, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ArcGisAudit");
        var cfg = options.Value;
        var subject = context.User.FindFirstValue("sub") ?? "unknown";
        var cid = context.TraceIdentifier;
        if (!applications.TryResolve(context.User, out var application))
        { Audit(logger, subject, "unknown", context.Request.Method, 403, 0, cid, "application_denied"); context.Response.StatusCode = 403; return; }

        if (!resources.TryResolve(path, out var resource))
        { Audit(logger, subject, path ?? "", context.Request.Method, 403, 0, cid, "resource_invalid"); context.Response.StatusCode = 403; return; }

        var normalized = resource.CanonicalPath;

        if (!cfg.AllowedPathPrefixes.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        { Audit(logger, subject, normalized, context.Request.Method, 403, 0, cid, "path_denied"); context.Response.StatusCode = 403; return; }

        var operation = resource.Operation;
        if (!operationPolicy.IsAllowed(resource, context.Request.Method))
        { Audit(logger, subject, normalized, context.Request.Method, 403, 0, cid, "operation_denied"); context.Response.StatusCode = 403; return; }

        var tenant = context.User.FindFirstValue("tenant_id") ?? context.User.FindFirstValue("tid");
        var rateCost = rateCostPolicy.GetCost(resource, context.Request.Query);
        var rateDecision = await gatewayRateLimiter.ConsumeAsync(
            new RateLimitKey(subject, tenant, application.Id, resource.ServiceName, resource.LayerId, resource.Operation),
            rateCost.Units, context.RequestAborted);
        if (!rateDecision.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = rateDecision.RetryAfterSeconds.ToString();
            telemetry.TryWrite(new TelemetryEvent(DateTimeOffset.UtcNow, subject, tenant, application.Id,
                resource.ServiceName, resource.ServiceType, resource.LayerId, resource.Operation,
                context.Request.Method, StatusCodes.Status429TooManyRequests, 0, "THROTTLE",
                rateDecision.ReasonCode, null, cid));
            return;
        }

        var accessRequest = new AccessPolicyRequest(
            subject, tenant, application.Id, resource.ServiceName, resource.ServiceType,
            resource.LayerId, resource.Operation, context.Request.Method, cid);

        var accessDecision = await accessPolicy.AuthorizeAsync(application, accessRequest, context.RequestAborted);
        if (!accessDecision.Allowed)
        {
            logger.LogWarning(
                "arcgis_access_denied earthid_sub={EarthIdSub} application={Application} service={Service} layer={LayerId} operation={Operation} reason={ReasonCode} policy_version={PolicyVersion} correlation_id={CorrelationId}",
                subject, application.Id, resource.ServiceName, resource.LayerId, resource.Operation,
                accessDecision.ReasonCode, accessDecision.PolicyVersion, cid);
            telemetry.TryWrite(new TelemetryEvent(DateTimeOffset.UtcNow, subject, tenant, application.Id,
                resource.ServiceName, resource.ServiceType, resource.LayerId, resource.Operation,
                context.Request.Method, StatusCodes.Status403Forbidden, 0, "DENY",
                accessDecision.ReasonCode, accessDecision.PolicyVersion, cid));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var baseUri = new Uri(cfg.ArcGisBaseUrl.TrimEnd('/') + "/");
        var target = new Uri(baseUri, normalized.TrimStart('/') + context.Request.QueryString);
        if (!string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 403; return; }

        byte[]? postBody = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms, context.RequestAborted);
            postBody = ms.ToArray();
        }

        var sw = Stopwatch.StartNew();
        var client = clients.CreateClient("arcgis");
        var result = await SendAsync(client, target, context, postBody,
            await tokens.GetServerTokenAsync(context.RequestAborted), cid);

        if (IsAuthFailure(result))
        {
            tokens.InvalidateServerToken();
            logger.LogWarning("arcgis_auth_retry earthid_sub={EarthIdSub} path={Path} correlation_id={CorrelationId}",
                subject, normalized, cid);
            result = await SendAsync(client, target, context, postBody,
                await tokens.GetServerTokenAsync(context.RequestAborted), cid);
        }

        context.Response.StatusCode = result.Status;
        if (result.ContentType is not null) context.Response.ContentType = result.ContentType;
        await context.Response.Body.WriteAsync(result.Body, context.RequestAborted);
        sw.Stop();
        Audit(logger, subject, normalized, context.Request.Method, result.Status, sw.ElapsedMilliseconds, cid, $"allow:{application.Id}:{resource.ServiceName}:{resource.LayerId}:{resource.Operation}");
        telemetry.TryWrite(new TelemetryEvent(DateTimeOffset.UtcNow, subject, tenant, application.Id,
            resource.ServiceName, resource.ServiceType, resource.LayerId, resource.Operation,
            context.Request.Method, result.Status, sw.ElapsedMilliseconds, "ALLOW",
            accessDecision.ReasonCode, accessDecision.PolicyVersion, cid));
    }

    private static async Task<UpstreamResult> SendAsync(HttpClient client, Uri target, HttpContext context,
        byte[]? postBody, string token, string cid)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        request.Headers.TryAddWithoutValidation("X-Esri-Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", cid);
        if (postBody is not null)
        {
            request.Content = new ByteArrayContent(postBody);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        return new UpstreamResult((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsByteArrayAsync(context.RequestAborted));
    }

    private static bool IsAuthFailure(UpstreamResult result)
    {
        if (result.Status is 401 or 498 or 499) return true;
        if (result.Body.Length == 0 || result.Body.Length > 1048576) return false;
        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && code.GetInt32() is 498 or 499;
        }
        catch (JsonException) { return false; }
    }

    private static void Audit(ILogger logger, string sub, string path, string method, int status, long ms, string cid, string decision) =>
        logger.LogInformation("arcgis_request earthid_sub={EarthIdSub} method={Method} path={Path} status={Status} duration_ms={DurationMs} decision={Decision} correlation_id={CorrelationId}",
            sub, method, path, status, ms, decision, cid);

    private sealed record UpstreamResult(int Status, string? ContentType, byte[] Body);
}