using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed class ClickHouseAdminAuditStore(
    IHttpClientFactory clients,
    IOptions<TelemetryPersistenceOptions> options,
    ILogger<ClickHouseAdminAuditStore> logger) : IAdminAuditStore
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly Regex IdentifierPattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly TelemetryPersistenceOptions cfg = options.Value;

    public async Task AddAsync(
        AdminEnforcementEvent item,
        CancellationToken cancellationToken)
    {
        var uri = BuildInsertUri();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(item, JsonOptions) + "\n",
                Encoding.UTF8,
                "application/x-ndjson")
        };
        AddCredentials(request);

        var client = clients.CreateClient("clickhouse");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<AdminEnforcementEvent>> GetRecentAsync(
        int max,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(max, 1, 1000);
        var uri = BuildQueryUri(limit);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddCredentials(request);

        var client = clients.CreateClient("clickhouse");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("ClickHouse admin-audit response exceeds the configured size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await ReadBoundedAsync(stream, cancellationToken);
        var events = new List<AdminEnforcementEvent>();
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = JsonSerializer.Deserialize<AdminEnforcementEvent>(line, JsonOptions);
            if (item is null)
                throw new InvalidOperationException("ClickHouse admin-audit response contains an invalid event.");
            events.Add(item);
        }

        logger.LogDebug("clickhouse_admin_audit_query rows={Rows}", events.Count);
        return events;
    }

    private Uri BuildInsertUri()
    {
        var baseUri = ValidateBaseUri();
        var sql = $"INSERT INTO {cfg.AdminAuditTable} FORMAT JSONEachRow";
        return BuildUri(baseUri, sql, null);
    }

    private Uri BuildQueryUri(int limit)
    {
        var baseUri = ValidateBaseUri();
        var sql = string.Join(
            '\n',
            "SELECT timestamp, adminSubject, action, targetSubject, reason, expiresAt, correlationId",
            $"FROM {cfg.AdminAuditTable}",
            "ORDER BY timestamp DESC",
            "LIMIT {limit:UInt32}",
            "FORMAT JSONEachRow");
        return BuildUri(baseUri, sql, limit);
    }

    private Uri ValidateBaseUri()
    {
        if (!Uri.TryCreate(cfg.ClickHouseBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !IdentifierPattern.IsMatch(cfg.Database) ||
            !IdentifierPattern.IsMatch(cfg.AdminAuditTable))
        {
            throw new InvalidOperationException("ClickHouse admin-audit configuration is invalid.");
        }

        return baseUri;
    }

    private Uri BuildUri(Uri baseUri, string sql, int? limit)
    {
        var parts = new List<string>
        {
            $"database={Uri.EscapeDataString(cfg.Database)}",
            $"query={Uri.EscapeDataString(sql)}",
            "date_time_input_format=best_effort"
        };
        if (limit is not null)
            parts.Add($"param_limit={limit.Value}");
        return new Uri(baseUri, "?" + string.Join('&', parts));
    }

    private void AddCredentials(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Username))
            request.Headers.TryAddWithoutValidation("X-ClickHouse-User", cfg.Username);
        if (!string.IsNullOrWhiteSpace(cfg.Password))
            request.Headers.TryAddWithoutValidation("X-ClickHouse-Key", cfg.Password);
    }

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var remaining = MaxResponseBytes + 1 - checked((int)buffer.Length);
            if (remaining <= 0)
                throw new InvalidOperationException("ClickHouse admin-audit response exceeds the configured size limit.");

            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                cancellationToken);
            if (read == 0)
                break;

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            if (buffer.Length > MaxResponseBytes)
                throw new InvalidOperationException("ClickHouse admin-audit response exceeds the configured size limit.");
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
