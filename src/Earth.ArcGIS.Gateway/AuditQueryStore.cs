using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed record AuditQuery(
    string? EarthIdSub = null,
    string? Application = null,
    string? Service = null,
    int? LayerId = null,
    string? Operation = null,
    string? Decision = null,
    int? StatusCode = null,
    string? CorrelationId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 250);

public interface IAuditQueryStore
{
    Task<IReadOnlyList<TelemetryEvent>> QueryAsync(
        AuditQuery query,
        CancellationToken cancellationToken);
}

public sealed class RecentAuditQueryStore(ITelemetryQueue telemetry)
    : IAuditQueryStore
{
    public Task<IReadOnlyList<TelemetryEvent>> QueryAsync(
        AuditQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<TelemetryEvent> events = telemetry.GetRecent(
            Math.Clamp(Math.Max(query.Limit, 1000), 1, 1000));

        if (!string.IsNullOrWhiteSpace(query.EarthIdSub))
            events = events.Where(x => x.EarthIdSub == query.EarthIdSub);
        if (!string.IsNullOrWhiteSpace(query.Application))
            events = events.Where(x => x.Application == query.Application);
        if (!string.IsNullOrWhiteSpace(query.Service))
            events = events.Where(x => x.Service == query.Service);
        if (query.LayerId is not null)
            events = events.Where(x => x.LayerId == query.LayerId);
        if (!string.IsNullOrWhiteSpace(query.Operation))
            events = events.Where(x => x.Operation == query.Operation);
        if (!string.IsNullOrWhiteSpace(query.Decision))
            events = events.Where(x => x.Decision == query.Decision);
        if (query.StatusCode is not null)
            events = events.Where(x => x.StatusCode == query.StatusCode);
        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            events = events.Where(x => x.CorrelationId == query.CorrelationId);
        if (query.From is not null)
            events = events.Where(x => x.Timestamp >= query.From);
        if (query.To is not null)
            events = events.Where(x => x.Timestamp <= query.To);

        IReadOnlyList<TelemetryEvent> result = events
            .OrderByDescending(x => x.Timestamp)
            .Take(Math.Clamp(query.Limit, 1, 1000))
            .ToArray();

        return Task.FromResult(result);
    }
}

public sealed class ClickHouseAuditQueryStore(
    IHttpClientFactory clients,
    IOptions<TelemetryPersistenceOptions> options,
    ILogger<ClickHouseAuditQueryStore> logger) : IAuditQueryStore
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly Regex IdentifierPattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TelemetryPersistenceOptions cfg = options.Value;

    public async Task<IReadOnlyList<TelemetryEvent>> QueryAsync(
        AuditQuery query,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var uri = BuildUri(query with { Limit = limit });

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddCredentials(request);

        var client = clients.CreateClient("clickhouse");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("ClickHouse audit response exceeds the configured size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await ReadBoundedAsync(stream, cancellationToken);
        var events = new List<TelemetryEvent>();

        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = JsonSerializer.Deserialize<TelemetryEvent>(line, JsonOptions);
            if (item is null)
                throw new InvalidOperationException("ClickHouse audit response contains an invalid event.");
            events.Add(item);
        }

        logger.LogDebug(
            "clickhouse_audit_query rows={Rows} limit={Limit}",
            events.Count,
            limit);
        return events;
    }

    private Uri BuildUri(AuditQuery query)
    {
        if (!Uri.TryCreate(cfg.ClickHouseBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !IdentifierPattern.IsMatch(cfg.Database) ||
            !IdentifierPattern.IsMatch(cfg.Table))
        {
            throw new InvalidOperationException("ClickHouse audit configuration is invalid.");
        }

        var where = new List<string> { "1=1" };
        var parameters = new List<KeyValuePair<string, string>>();

        AddStringFilter(where, parameters, "earthIdSub", "sub", query.EarthIdSub);
        AddStringFilter(where, parameters, "application", "application", query.Application);
        AddStringFilter(where, parameters, "service", "service", query.Service);
        AddStringFilter(where, parameters, "operation", "operation", query.Operation);
        AddStringFilter(where, parameters, "decision", "decision", query.Decision);
        AddStringFilter(where, parameters, "correlationId", "correlation", query.CorrelationId);

        if (query.LayerId is not null)
        {
            where.Add("layerId = {layer:Int32}");
            parameters.Add(new("param_layer", query.LayerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (query.StatusCode is not null)
        {
            where.Add("statusCode = {status:UInt16}");
            parameters.Add(new("param_status", query.StatusCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (query.From is not null)
        {
            where.Add("timestamp >= parseDateTime64BestEffort({from:String}, 3, 'UTC')");
            parameters.Add(new("param_from", query.From.Value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (query.To is not null)
        {
            where.Add("timestamp <= parseDateTime64BestEffort({to:String}, 3, 'UTC')");
            parameters.Add(new("param_to", query.To.Value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var sql = $"""
SELECT
 timestamp, earthIdSub, tenant, application, service, serviceType, layerId,
 operation, method, statusCode, durationMs, decision, reasonCode, policyVersion,
 correlationId, responseBytes, rateLimitRemaining, activityClass, rateCostUnits,
 resourceRateRemaining, rateProfileVersion, concurrencyClass, isSpatial,
 returnsGeometry, isPaged, isHeavy, isExtractionLike, queryFingerprint, recordCount
FROM {cfg.Table}
WHERE {string.Join(" AND ", where)}
ORDER BY timestamp DESC
LIMIT {{limit:UInt32}}
FORMAT JSONEachRow
""";

        parameters.Add(new("database", cfg.Database));
        parameters.Add(new("query", sql));
        parameters.Add(new("param_limit", query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var queryString = string.Join(
            '&',
            parameters.Select(x =>
                $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return new Uri(baseUri, "?" + queryString);
    }

    private static void AddStringFilter(
        ICollection<string> where,
        ICollection<KeyValuePair<string, string>> parameters,
        string column,
        string parameter,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        where.Add($"{column} = {{{parameter}:String}}");
        parameters.Add(new($"param_{parameter}", value));
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
                throw new InvalidOperationException("ClickHouse audit response exceeds the configured size limit.");

            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                cancellationToken);
            if (read == 0)
                break;

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            if (buffer.Length > MaxResponseBytes)
                throw new InvalidOperationException("ClickHouse audit response exceeds the configured size limit.");
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
