using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed class TelemetryPersistenceOptions
{
    public string ClickHouseBaseUrl { get; set; } = "";
    public string Database { get; set; } = "default";
    public string Table { get; set; } = "gateway_telemetry";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string SpoolDirectory { get; set; } = "data/telemetry-spool";
    public int MaxSpoolFiles { get; set; } = 1_000;
    public long MaxSpoolBytes { get; set; } = 512L * 1024 * 1024;
    public int ReplayBatchFiles { get; set; } = 10;
}

public sealed class TelemetryPersistenceHealth
{
    private long persistedBatches;
    private long spooledBatches;
    private long replayedBatches;
    private long droppedSpoolBatches;
    private long quarantinedSpoolFiles;
    private long storageFailures;
    private long lastSuccessUnixMs;
    private long lastFailureUnixMs;

    public long PersistedBatches => Interlocked.Read(ref persistedBatches);
    public long SpooledBatches => Interlocked.Read(ref spooledBatches);
    public long ReplayedBatches => Interlocked.Read(ref replayedBatches);
    public long DroppedSpoolBatches => Interlocked.Read(ref droppedSpoolBatches);
    public long QuarantinedSpoolFiles => Interlocked.Read(ref quarantinedSpoolFiles);
    public long StorageFailures => Interlocked.Read(ref storageFailures);
    public DateTimeOffset? LastSuccess => ReadTimestamp(lastSuccessUnixMs);
    public DateTimeOffset? LastFailure => ReadTimestamp(lastFailureUnixMs);

    public void MarkPersisted()
    {
        Interlocked.Increment(ref persistedBatches);
        Interlocked.Exchange(ref lastSuccessUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void MarkSpooled() => Interlocked.Increment(ref spooledBatches);
    public void MarkReplayed() => Interlocked.Increment(ref replayedBatches);
    public void MarkSpoolDropped() => Interlocked.Increment(ref droppedSpoolBatches);
    public void MarkSpoolQuarantined() => Interlocked.Increment(ref quarantinedSpoolFiles);

    public void MarkStorageFailure()
    {
        Interlocked.Increment(ref storageFailures);
        Interlocked.Exchange(ref lastFailureUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static DateTimeOffset? ReadTimestamp(long value)
    {
        var current = Interlocked.Read(ref value);
        return current == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(current);
    }
}

public sealed class ClickHouseTelemetrySink(
    IHttpClientFactory clients,
    IOptions<TelemetryPersistenceOptions> options) : ITelemetrySink
{
    private static readonly Regex IdentifierPattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TelemetryPersistenceOptions cfg = options.Value;

    public async Task WriteBatchAsync(
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return;

        if (!TryBuildInsertUri(out var uri))
            throw new InvalidOperationException("ClickHouse telemetry configuration is invalid.");

        var body = new StringBuilder();
        foreach (var item in events)
            body.AppendLine(JsonSerializer.Serialize(item, JsonOptions));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson")
        };

        if (!string.IsNullOrWhiteSpace(cfg.Username))
            request.Headers.TryAddWithoutValidation("X-ClickHouse-User", cfg.Username);
        if (!string.IsNullOrWhiteSpace(cfg.Password))
            request.Headers.TryAddWithoutValidation("X-ClickHouse-Key", cfg.Password);

        var client = clients.CreateClient("clickhouse");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private bool TryBuildInsertUri(out Uri uri)
    {
        uri = null!;

        if (!Uri.TryCreate(cfg.ClickHouseBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !IdentifierPattern.IsMatch(cfg.Database) ||
            !IdentifierPattern.IsMatch(cfg.Table))
            return false;

        var query = $"INSERT INTO {cfg.Table} FORMAT JSONEachRow";
        var relative =
            $"?database={Uri.EscapeDataString(cfg.Database)}" +
            $"&query={Uri.EscapeDataString(query)}" +
            "&date_time_input_format=best_effort";
        uri = new Uri(baseUri, relative);
        return true;
    }
}

public sealed class FileTelemetrySpool(
    IOptions<TelemetryPersistenceOptions> options,
    TelemetryPersistenceHealth health,
    ILogger<FileTelemetrySpool> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly TelemetryPersistenceOptions cfg = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task AppendAsync(
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
            return;

        await gate.WaitAsync(cancellationToken);
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(cfg.SpoolDirectory);

            var finalPath = Path.Combine(
                cfg.SpoolDirectory,
                $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jsonl");
            tempPath = finalPath + ".tmp";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var item in events)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions));

                await writer.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, finalPath);
            tempPath = null;
            health.MarkSpooled();
            BoundSpoolDirectory();
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "telemetry_spool_temp_cleanup_failed file={File}",
                        Path.GetFileName(tempPath));
                }
            }

            gate.Release();
        }
    }

    public async Task ReplayAsync(
        ITelemetrySink destination,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(cfg.SpoolDirectory))
                return;

            var files = Directory
                .EnumerateFiles(cfg.SpoolDirectory, "*.jsonl")
                .OrderBy(x => x, StringComparer.Ordinal)
                .Take(Math.Max(1, cfg.ReplayBatchFiles))
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    var events = new List<TelemetryEvent>();
                    foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var item = JsonSerializer.Deserialize<TelemetryEvent>(line, JsonOptions);
                        if (item is null)
                        {
                            throw new JsonException(
                                $"Telemetry spool file '{Path.GetFileName(file)}' contains a null event.");
                        }
                        events.Add(item);
                    }

                    if (events.Count > 0)
                        await destination.WriteBatchAsync(events, cancellationToken);

                    File.Delete(file);
                    health.MarkReplayed();
                }
                catch (JsonException ex)
                {
                    Quarantine(file, ex);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public int CountPendingFiles()
    {
        if (!Directory.Exists(cfg.SpoolDirectory))
            return 0;

        return Directory.EnumerateFiles(cfg.SpoolDirectory, "*.jsonl").Count();
    }

    public long CountPendingBytes()
    {
        if (!Directory.Exists(cfg.SpoolDirectory))
            return 0;

        return Directory
            .EnumerateFiles(cfg.SpoolDirectory, "*.jsonl")
            .Sum(path => new FileInfo(path).Length);
    }

    private void BoundSpoolDirectory()
    {
        var maxFiles = Math.Max(1, cfg.MaxSpoolFiles);
        var maxBytes = Math.Max(1L, cfg.MaxSpoolBytes);
        var files = Directory
            .EnumerateFiles(cfg.SpoolDirectory, "*.jsonl")
            .Select(path => new FileInfo(path))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
        var bytes = files.Sum(x => x.Length);

        while (files.Count > maxFiles || bytes > maxBytes)
        {
            var oldest = files[0];
            files.RemoveAt(0);
            bytes -= oldest.Length;
            File.Delete(oldest.FullName);
            health.MarkSpoolDropped();
            logger.LogError(
                "telemetry_spool_capacity_exceeded dropped_file={File} remaining_bytes={RemainingBytes}",
                oldest.Name,
                bytes);
        }
    }

    private void Quarantine(string file, Exception exception)
    {
        var quarantineDirectory = Path.Combine(cfg.SpoolDirectory, "quarantine");
        Directory.CreateDirectory(quarantineDirectory);
        var destination = Path.Combine(
            quarantineDirectory,
            $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid():N}.invalid.jsonl");
        File.Move(file, destination);
        health.MarkSpoolQuarantined();
        logger.LogError(
            exception,
            "telemetry_spool_corrupt quarantined_file={File}",
            Path.GetFileName(destination));
    }
}

public sealed class ResilientTelemetrySink(
    ClickHouseTelemetrySink primary,
    FileTelemetrySpool spool,
    TelemetryPersistenceHealth health,
    ILogger<ResilientTelemetrySink> logger) : ITelemetrySink
{
    public async Task WriteBatchAsync(
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            await spool.ReplayAsync(primary, cancellationToken);
            await primary.WriteBatchAsync(events, cancellationToken);
            health.MarkPersisted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            health.MarkStorageFailure();
            logger.LogError(ex, "telemetry_storage_unavailable spooling_count={Count}", events.Count);
            await spool.AppendAsync(events, CancellationToken.None);
        }
    }
}
