using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public interface IArcGisCredentialProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
    void InvalidateToken();
}

public sealed class ArcGisTokenProvider(
    IHttpClientFactory clients,
    IOptions<GatewayOptions> options,
    ILogger<ArcGisTokenProvider> logger) : IArcGisCredentialProvider
{
    private readonly GatewayOptions cfg = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenState? portalToken;
    private TokenState? serverToken;

    public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
        GetServerTokenAsync(cancellationToken);

    public void InvalidateToken() => InvalidateServerToken();

    public async Task<string> GetServerTokenAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref serverToken);
        if (Fresh(current))
            return current!.Value;

        await gate.WaitAsync(ct);
        try
        {
            current = Volatile.Read(ref serverToken);
            if (Fresh(current))
                return current!.Value;

            var portal = await GetPortalTokenAsync(ct);
            var refreshed = await ExchangeAsync(portal, ct);
            Volatile.Write(ref serverToken, refreshed);

            logger.LogInformation(
                "federated_server_token_refreshed expires_at={ExpiresAt}",
                refreshed.ExpiresAt);

            return refreshed.Value;
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateServerToken() => Interlocked.Exchange(ref serverToken, null);

    private bool Fresh(TokenState? token) =>
        token is not null &&
        token.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, cfg.RefreshSkewSeconds));

    private async Task<string> GetPortalTokenAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref portalToken);
        if (Fresh(current))
            return current!.Value;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = cfg.Username,
            ["password"] = cfg.Password,
            ["client"] = "requestip",
            ["expiration"] = cfg.TokenExpirationMinutes.ToString(CultureInfo.InvariantCulture),
            ["f"] = "json"
        });

        var refreshed = await RequestAsync(form, "portal", ct);
        Volatile.Write(ref portalToken, refreshed);

        logger.LogInformation(
            "portal_token_refreshed expires_at={ExpiresAt}",
            refreshed.ExpiresAt);

        return refreshed.Value;
    }

    private async Task<TokenState> ExchangeAsync(string portal, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = portal,
            ["serverUrl"] = cfg.FederatedServerUrl,
            ["f"] = "json"
        });

        return await RequestAsync(form, "federated-server", ct);
    }

    private async Task<TokenState> RequestAsync(HttpContent form, string kind, CancellationToken ct)
    {
        if (!Uri.TryCreate(cfg.PortalTokenEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("ArcGIS portal token endpoint must be an absolute HTTPS URL.");
        }

        var http = clients.CreateClient("arcgis-token");
        using var response = await http.PostAsync(endpoint, form, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("error", out _))
            throw new InvalidOperationException($"ArcGIS {kind} token request returned an error.");

        if (!doc.RootElement.TryGetProperty("token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()) ||
            !doc.RootElement.TryGetProperty("expires", out var expiresElement) ||
            !TryReadEpochMilliseconds(expiresElement, out var expiresMs))
        {
            throw new InvalidOperationException($"ArcGIS {kind} token response is invalid.");
        }

        return new TokenState(
            tokenElement.GetString()!,
            DateTimeOffset.FromUnixTimeMilliseconds(expiresMs));
    }

    private static bool TryReadEpochMilliseconds(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt64(out value);

        if (element.ValueKind == JsonValueKind.String)
            return long.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);

        value = default;
        return false;
    }

    private sealed record TokenState(string Value, DateTimeOffset ExpiresAt);
}
