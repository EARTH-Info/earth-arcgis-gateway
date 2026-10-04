using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed class OAuthArcGisCredentialProvider(
    IHttpClientFactory clients,
    IOptions<GatewayOptions> options,
    ILogger<OAuthArcGisCredentialProvider> logger) : IArcGisCredentialProvider
{
    private readonly GatewayOptions cfg = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenState? token;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref token);
        if (Fresh(current))
            return current!.Value;

        await gate.WaitAsync(cancellationToken);
        try
        {
            current = Volatile.Read(ref token);
            if (Fresh(current))
                return current!.Value;

            var refreshed = await RequestTokenAsync(cancellationToken);
            Volatile.Write(ref token, refreshed);

            logger.LogInformation(
                "arcgis_oauth_token_refreshed expires_at={ExpiresAt}",
                refreshed.ExpiresAt);

            return refreshed.Value;
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateToken() => Interlocked.Exchange(ref token, null);

    private bool Fresh(TokenState? current) =>
        current is not null &&
        current.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, cfg.RefreshSkewSeconds));

    private async Task<TokenState> RequestTokenAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(cfg.OAuthTokenEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("ArcGIS OAuth token endpoint must be an absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(cfg.OAuthClientId) ||
            string.IsNullOrWhiteSpace(cfg.OAuthClientSecret))
        {
            throw new InvalidOperationException("ArcGIS OAuth client credentials are not configured.");
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = cfg.OAuthClientId,
            ["client_secret"] = cfg.OAuthClientSecret
        });

        var http = clients.CreateClient("arcgis-oauth");
        using var response = await http.PostAsync(endpoint, form, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out _))
            throw new InvalidOperationException("ArcGIS OAuth token endpoint returned an error.");

        if (!root.TryGetProperty("access_token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()) ||
            !root.TryGetProperty("expires_in", out var expiresElement) ||
            !expiresElement.TryGetInt32(out var expiresInSeconds) ||
            expiresInSeconds <= 0)
        {
            throw new InvalidOperationException("ArcGIS OAuth token response is invalid.");
        }

        return new TokenState(
            tokenElement.GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds));
    }

    private sealed record TokenState(string Value, DateTimeOffset ExpiresAt);
}
