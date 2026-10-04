namespace Earth.ArcGIS.Gateway;

public interface ITelemetrySink
{
    Task WriteBatchAsync(IReadOnlyList<TelemetryEvent> events, CancellationToken cancellationToken);
}

public sealed class LoggingTelemetrySink(ILogger<LoggingTelemetrySink> logger) : ITelemetrySink
{
    public Task WriteBatchAsync(IReadOnlyList<TelemetryEvent> events, CancellationToken cancellationToken)
    {
        logger.LogInformation("telemetry_batch count={Count}", events.Count);
        return Task.CompletedTask;
    }
}

public sealed class TelemetryWorker(
    ITelemetryQueue queue,
    ITelemetrySink sink,
    ILogger<TelemetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<TelemetryEvent>(256);
        try
        {
            await foreach (var item in queue.ReadAllAsync(stoppingToken))
            {
                batch.Add(item);
                if (batch.Count < 256) continue;
                await FlushAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (batch.Count > 0)
            {
                try { await FlushAsync(batch, CancellationToken.None); }
                catch (Exception ex) { logger.LogError(ex, "telemetry_final_flush_failed count={Count}", batch.Count); }
            }
        }
    }

    private async Task FlushAsync(List<TelemetryEvent> batch, CancellationToken cancellationToken)
    {
        var snapshot = batch.ToArray();
        batch.Clear();
        try { await sink.WriteBatchAsync(snapshot, cancellationToken); }
        catch (Exception ex)
        {
            logger.LogError(ex, "telemetry_batch_failed count={Count}", snapshot.Length);
        }
    }
}
