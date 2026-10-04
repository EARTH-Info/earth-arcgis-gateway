using System.Net.Http.Json;
using System.Text.Json;

namespace Earth.ArcGIS.Gateway;

public sealed record AccessPolicyRequest(
    string EarthIdSub,
    string? Tenant,
    string Application,
    string Service,
    string ServiceType,
    int? LayerId,
    string Operation,
    string Method,
    string CorrelationId);

public sealed record AccessPolicyDecision(bool Allowed, string ReasonCode, string? PolicyVersion);

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
    public async Task<AccessPolicyDecision> AuthorizeAsync(
        GatewayApplication application,
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(application.AccessApiAuthorizeUrl))
            return Deny("access_api_not_configured");

        if (!Uri.TryCreate(application.AccessApiAuthorizeUrl, UriKind.Absolute, out var authorizeUri) ||
            authorizeUri.Scheme != Uri.UriSchemeHttps)
            return Deny("access_api_invalid_configuration");

        try
        {
            var client = clients.CreateClient("access-policy");
            using var message = new HttpRequestMessage(HttpMethod.Post, authorizeUri)
            {
                Content = JsonContent.Create(request)
            };
            message.Headers.TryAddWithoutValidation("X-Correlation-ID", request.CorrelationId);

            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Deny("access_api_http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = document.RootElement;
            if (!root.TryGetProperty("decision", out var decisionElement) ||
                decisionElement.ValueKind != JsonValueKind.String)
                return Deny("access_api_malformed");

            var decision = decisionElement.GetString();
            var reason = root.TryGetProperty("reasonCode", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() ?? "unspecified"
                : "unspecified";
            var policyVersion = root.TryGetProperty("policyVersion", out var versionElement) &&
                                versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;

            return decision switch
            {
                "ALLOW" => new AccessPolicyDecision(true, reason, policyVersion),
                "DENY" => Deny(reason, policyVersion),
                _ => Deny("access_api_unknown_decision", policyVersion)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("access_policy_timeout application={Application} correlation_id={CorrelationId}",
                request.Application, request.CorrelationId);
            return Deny("access_api_timeout");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "access_policy_unavailable application={Application} correlation_id={CorrelationId}",
                request.Application, request.CorrelationId);
            return Deny("access_api_unavailable");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "access_policy_malformed application={Application} correlation_id={CorrelationId}",
                request.Application, request.CorrelationId);
            return Deny("access_api_malformed");
        }
    }

    private static AccessPolicyDecision Deny(string reason, string? version = null) =>
        new(false, reason, version);
}
