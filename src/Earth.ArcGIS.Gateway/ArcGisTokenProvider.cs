using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed class ArcGisTokenProvider(HttpClient http, IOptions<GatewayOptions> options)
{
    private readonly GatewayOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return _token;

        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return _token;

            using var content = new FormUrlEncodedContent(new Dictionary<string,string>
            {
                ["username"] = _options.Username,
                ["password"] = _options.Password,
                ["client"] = "requestip",
                ["expiration"] = _options.TokenExpirationMinutes.ToString(),
                ["f"] = "json"
            });
            using var response = await http.PostAsync(_options.TokenEndpoint, content, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));

            if (!doc.RootElement.TryGetProperty("token", out var token))
                throw new InvalidOperationException("ArcGIS token endpoint did not return a token.");

            _token = token.GetString()!;
            _expiresAt = doc.RootElement.TryGetProperty("expires", out var expires)
                ? DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64())
                : DateTimeOffset.UtcNow.AddMinutes(_options.TokenExpirationMinutes);
            return _token;
        }
        finally { _lock.Release(); }
    }
}