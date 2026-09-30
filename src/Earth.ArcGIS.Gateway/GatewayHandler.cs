using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public static class GatewayHandler
{
    private static readonly HashSet<string> BlockedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "applyEdits", "addFeatures", "updateFeatures", "deleteFeatures",
        "calculate", "append", "truncate", "upload", "deleteFromDefinition",
        "addToDefinition", "updateDefinition"
    };

    public static async Task HandleAsync(
        HttpContext context, string? path, IHttpClientFactory clients,
        ArcGisTokenProvider tokens, IOptions<GatewayOptions> options, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ArcGisAudit");
        var cfg = options.Value;
        var subject = context.User.FindFirstValue("sub") ?? "unknown";
        var correlationId = context.TraceIdentifier;
        var normalized = "/" + (path ?? "").TrimStart('/');

        if (!cfg.AllowedPathPrefixes.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            Audit(logger, subject, normalized, context.Request.Method, 403, 0, correlationId, "path_denied");
            context.Response.StatusCode = 403; return;
        }

        var operation = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (BlockedOperations.Contains(operation))
        {
            Audit(logger, subject, normalized, context.Request.Method, 403, 0, correlationId, "write_denied");
            context.Response.StatusCode = 403; return;
        }

        var baseUri = new Uri(cfg.ArcGisBaseUrl.TrimEnd('/') + "/");
        var target = new Uri(baseUri, normalized.TrimStart('/') + context.Request.QueryString);
        if (!string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 403; return; }

        var sw = Stopwatch.StartNew();
        var client = clients.CreateClient("arcgis");
        using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        upstream.Headers.TryAddWithoutValidation("X-Esri-Authorization",
            "Bearer " + await tokens.GetServerTokenAsync(context.RequestAborted));
        upstream.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);

        if (HttpMethods.IsPost(context.Request.Method))
        {
            upstream.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                upstream.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }

        using var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is not null)
            context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        sw.Stop();
        Audit(logger, subject, normalized, context.Request.Method, (int)response.StatusCode, sw.ElapsedMilliseconds, correlationId, "allow");
    }

    private static void Audit(ILogger logger, string sub, string path, string method, int status, long elapsedMs, string correlationId, string decision) =>
        logger.LogInformation("arcgis_request earthid_sub={EarthIdSub} method={Method} path={Path} status={Status} duration_ms={DurationMs} decision={Decision} correlation_id={CorrelationId}",
            sub, method, path, status, elapsedMs, decision, correlationId);
}