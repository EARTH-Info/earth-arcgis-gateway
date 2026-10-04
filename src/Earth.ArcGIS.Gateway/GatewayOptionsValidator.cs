using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var errors = new List<string>();

        if (!IsHttpsOrigin(options.ArcGisBaseUrl))
            errors.Add("Gateway:ArcGisBaseUrl must be an HTTPS origin with no path, query, fragment, or user-info.");

        if (options.AllowedPathPrefixes.Length == 0 ||
            options.AllowedPathPrefixes.Any(p => !IsSafeAllowedPrefix(p)))
        {
            errors.Add(
                "Gateway:AllowedPathPrefixes must contain canonical, explicit /arcgis/rest/services/... prefixes.");
        }

        if (options.RefreshSkewSeconds < 30)
            errors.Add("Gateway:RefreshSkewSeconds must be at least 30 seconds.");

        switch (options.CredentialProvider.Trim().ToLowerInvariant())
        {
            case "federated-11.3":
                if (!IsHttpsAbsolute(options.PortalTokenEndpoint))
                    errors.Add("Gateway:PortalTokenEndpoint must be an absolute HTTPS URL.");
                if (!IsHttpsAbsolute(options.FederatedServerUrl))
                    errors.Add("Gateway:FederatedServerUrl must be an absolute HTTPS URL.");
                if (string.IsNullOrWhiteSpace(options.Username))
                    errors.Add("Gateway:Username is required for federated-11.3.");
                if (string.IsNullOrWhiteSpace(options.Password))
                    errors.Add("Gateway:Password is required for federated-11.3.");
                if (options.TokenExpirationMinutes <= 0)
                    errors.Add("Gateway:TokenExpirationMinutes must be positive.");
                break;

            case "oauth-11.5":
                if (!IsHttpsAbsolute(options.OAuthTokenEndpoint))
                    errors.Add("Gateway:OAuthTokenEndpoint must be an absolute HTTPS URL.");
                if (string.IsNullOrWhiteSpace(options.OAuthClientId))
                    errors.Add("Gateway:OAuthClientId is required for oauth-11.5.");
                if (string.IsNullOrWhiteSpace(options.OAuthClientSecret))
                    errors.Add("Gateway:OAuthClientSecret is required for oauth-11.5.");

                if (options.OAuthExchangeForFederatedServer)
                {
                    if (!IsHttpsAbsolute(options.PortalTokenEndpoint))
                        errors.Add("Gateway:PortalTokenEndpoint is required when OAuth federated exchange is enabled.");
                    if (!IsHttpsAbsolute(options.FederatedServerUrl))
                        errors.Add("Gateway:FederatedServerUrl is required when OAuth federated exchange is enabled.");
                }
                break;

            default:
                errors.Add("Gateway:CredentialProvider must be 'federated-11.3' or 'oauth-11.5'.");
                break;
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsSafeAllowedPrefix(string value)
    {
        const string root = "/arcgis/rest/services/";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            value.Length <= root.Length)
            return false;

        var normalized = value.TrimEnd('/');
        return normalized.Length > root.TrimEnd('/').Length &&
               !normalized.Contains("..", StringComparison.Ordinal) &&
               !normalized.Contains('\\') &&
               !normalized.Contains("//", StringComparison.Ordinal) &&
               !normalized.Contains('%') &&
               !normalized.Contains('?') &&
               !normalized.Contains('#') &&
               !normalized.Contains(':');
    }

    private static bool IsHttpsAbsolute(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsHttpsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == "/";
}

public sealed class ApplicationOptionsValidator : IValidateOptions<ApplicationOptions>
{
    public ValidateOptionsResult Validate(string? name, ApplicationOptions options)
    {
        var errors = new List<string>();

        if (options.Registrations.Count == 0)
            errors.Add("At least one application registration is required.");

        foreach (var (id, registration) in options.Registrations)
        {
            if (string.IsNullOrWhiteSpace(id))
                errors.Add("Application registration IDs cannot be empty.");

            if (registration.Audiences.Length == 0 ||
                registration.Audiences.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Applications:{id}:Audiences must contain at least one non-empty audience.");
            }

            if (registration.ClientIds.Length == 0 ||
                registration.ClientIds.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Applications:{id}:ClientIds must contain at least one trusted client ID.");
            }

            if (string.IsNullOrWhiteSpace(registration.AccessApiAuthorizeUrl) ||
                !Uri.TryCreate(registration.AccessApiAuthorizeUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                errors.Add($"Applications:{id}:AccessApiAuthorizeUrl must be an absolute HTTPS URL.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
