using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Earth.ArcGIS.Gateway;

public sealed record AccessPolicyRequest(
    string EarthIdSub,
    string? Tenant,
    [property: JsonPropertyName("applicationId")] string Application,
    string Service,
    string ServiceType,
    int? LayerId,
    string Operation,
    string Method,
    string CorrelationId);

public sealed record AccessPolicyDecision(
    bool Allowed,
    string ReasonCode,
    string? PolicyVersion,
    int? CacheTtlSeconds = null,
    JsonElement? RateLimitProfile = null);

public interface IAccessPolicyClient
{
    Task<AccessPolicyDecision> AuthorizeAsync(
        GatewayApplication application,
        AccessPolicyRequest request,
        CancellationToken cancellationToken);
}

public sealed class AccessPolicyClient(
    IHttpClientFactory clients,
    ILogger<AccessPolicyClient> logger) : IAccessPolicyClient
{
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxCacheTtlSeconds = 300;

    public async Task<AccessPolicyDecision> AuthorizeAsync(
        GatewayApplication application,
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(application.AccessApiAuthorizeUrl))
            return Deny("access_api_not_configured");

        if (!Uri.TryCreate(application.AccessApiAuthorizeUrl, UriKind.Absolute, out var authorizeUri) ||
            authorizeUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(authorizeUri.UserInfo) ||
            !string.IsNullOrEmpty(authorizeUri.Fragment))
        {
            return Deny("access_api_invalid_configuration");
        }

        try
        {
            var client = clients.CreateClient("access-policy");
            using var message = new HttpRequestMessage(HttpMethod.Post, authorizeUri)
            {
                Content = JsonContent.Create(request)
            };
            message.Headers.TryAddWithoutValidation("X-Correlation-ID", request.CorrelationId);

            using var response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Deny("access_api_http_error");

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return Deny("access_api_response_too_large");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await ReadBoundedJsonAsync(stream, cancellationToken);

            var root = document.RootElement;
            if (!root.TryGetProperty("allowed", out var allowedElement) ||
                allowedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Deny("access_api_malformed");
            }

            var reason = root.TryGetProperty("reasonCode", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() ?? "unspecified"
                : "unspecified";

            var policyVersion = root.TryGetProperty("policyVersion", out var versionElement) &&
                                versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;

            int? cacheTtlSeconds = null;
            if (root.TryGetProperty("cacheTtlSeconds", out var ttlElement))
            {
                if (ttlElement.ValueKind != JsonValueKind.Number ||
                    !ttlElement.TryGetInt32(out var ttl) ||
                    ttl < 0 ||
                    ttl > MaxCacheTtlSeconds)
                {
                    return Deny("access_api_malformed", policyVersion);
                }

                cacheTtlSeconds = ttl;
            }

            JsonElement? rateLimitProfile = null;
            if (root.TryGetProperty("rateLimitProfile", out var profileElement) &&
                profileElement.ValueKind is not JsonValueKind.Null)
            {
                if (profileElement.ValueKind != JsonValueKind.Object)
                    return Deny("access_api_malformed", policyVersion);

                rateLimitProfile = profileElement.Clone();
            }

            return allowedElement.GetBoolean()
                ? new AccessPolicyDecision(
                    true,
                    reason,
                    policyVersion,
                    cacheTtlSeconds,
                    rateLimitProfile)
                : new AccessPolicyDecision(
                    false,
                    reason,
                    policyVersion,
                    cacheTtlSeconds,
                    rateLimitProfile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "access_policy_timeout application={Application} correlation_id={CorrelationId}",
                request.Application,
                request.CorrelationId);
            return Deny("access_api_timeout");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "access_policy_unavailable application={Application} correlation_id={CorrelationId}",
                request.Application,
                request.CorrelationId);
            return Deny("access_api_unavailable");
        }
        catch (AccessPolicyResponseTooLargeException)
        {
            logger.LogWarning(
                "access_policy_response_too_large application={Application} correlation_id={CorrelationId}",
                request.Application,
                request.CorrelationId);
            return Deny("access_api_response_too_large");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "access_policy_malformed application={Application} correlation_id={CorrelationId}",
                request.Application,
                request.CorrelationId);
            return Deny("access_api_malformed");
        }
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var remaining = MaxResponseBytes + 1 - checked((int)buffer.Length);
            if (remaining <= 0)
                throw new AccessPolicyResponseTooLargeException();

            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                cancellationToken);

            if (read == 0)
                break;

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            if (buffer.Length > MaxResponseBytes)
                throw new AccessPolicyResponseTooLargeException();
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(
            buffer,
            cancellationToken: cancellationToken);
    }

    private static AccessPolicyDecision Deny(
        string reason,
        string? version = null) =>
        new(false, reason, version);

    private sealed class AccessPolicyResponseTooLargeException : Exception;
}
