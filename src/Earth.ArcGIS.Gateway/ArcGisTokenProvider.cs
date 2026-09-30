using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed class ArcGisTokenProvider(HttpClient http, IOptions<GatewayOptions> options, ILogger<ArcGisTokenProvider> logger)
{
    private readonly GatewayOptions cfg = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenState? portalToken;
    private TokenState? serverToken;

    public async Task<string> GetServerTokenAsync(CancellationToken ct)
    {
        if (Fresh(serverToken)) return serverToken!.Value;
        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(serverToken)) return serverToken!.Value;
            var portal = await GetPortalTokenAsync(ct);
            serverToken = await ExchangeAsync(portal, ct);
            logger.LogInformation("federated_server_token_refreshed expires_at={ExpiresAt}", serverToken.ExpiresAt);
            return serverToken.Value;
        }
        finally { gate.Release(); }
    }

    public void InvalidateServerToken() => serverToken = null;

    private bool Fresh(TokenState? t) =>
        t is not null && t.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, cfg.RefreshSkewSeconds));

    private async Task<string> GetPortalTokenAsync(CancellationToken ct)
    {
        if (Fresh(portalToken)) return portalToken!.Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["username"] = cfg.Username, ["password"] = cfg.Password,
            ["client"] = "requestip", ["expiration"] = cfg.TokenExpirationMinutes.ToString(), ["f"] = "json"
        });
        portalToken = await RequestAsync(form, "portal", ct);
        logger.LogInformation("portal_token_refreshed expires_at={ExpiresAt}", portalToken.ExpiresAt);
        return portalToken.Value;
    }

    private async Task<TokenState> ExchangeAsync(string portal, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["token"] = portal, ["serverUrl"] = cfg.FederatedServerUrl, ["f"] = "json"
        });
        return await RequestAsync(form, "federated-server", ct);
    }

    private async Task<TokenState> RequestAsync(HttpContent form, string kind, CancellationToken ct)
    {
        using var response = await http.PostAsync(cfg.PortalTokenEndpoint, form, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"ArcGIS {kind} token request failed: {error}");
        if (!doc.RootElement.TryGetProperty("token", out var token) || !doc.RootElement.TryGetProperty("expires", out var expires))
            throw new InvalidOperationException($"ArcGIS {kind} token response is incomplete.");
        var ms = expires.ValueKind == JsonValueKind.String ? long.Parse(expires.GetString()!) : expires.GetInt64();
        return new TokenState(token.GetString()!, DateTimeOffset.FromUnixTimeMilliseconds(ms));
    }

    private sealed record TokenState(string Value, DateTimeOffset ExpiresAt);
}