namespace Earth.ArcGIS.Gateway;

public interface ITelemetrySink
{
    Task WriteBatchAsync(
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken);
}

public sealed class LoggingTelemetrySink(
    ILogger<LoggingTelemetrySink> logger) : ITelemetrySink
{
    public Task WriteBatchAsync(
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken)
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
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 256;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<TelemetryEvent>(BatchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var waitToRead = queue.WaitToReadAsync(stoppingToken).AsTask();
                var flushDelay = Task.Delay(FlushInterval, stoppingToken);
                var completed = await Task.WhenAny(waitToRead, flushDelay);

                if (completed == waitToRead && await waitToRead)
                {
                    while (batch.Count < BatchSize && queue.TryRead(out var item))
                    {
                        if (item is not null)
                            batch.Add(item);
                    }
                }

                if (batch.Count >= BatchSize ||
                    (completed == flushDelay && batch.Count > 0))
                {
                    await FlushAsync(batch, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (batch.Count > 0)
            {
                try
                {
                    await FlushAsync(batch, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "telemetry_final_flush_failed count={Count}",
                        batch.Count);
                }
            }
        }
    }

    private async Task FlushAsync(
        List<TelemetryEvent> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        var snapshot = batch.ToArray();
        await sink.WriteBatchAsync(snapshot, cancellationToken);
        batch.RemoveRange(0, snapshot.Length);
    }
}
