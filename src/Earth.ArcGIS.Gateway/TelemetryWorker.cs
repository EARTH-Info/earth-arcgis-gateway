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
        var nextFlush = DateTimeOffset.UtcNow + FlushInterval;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (batch.Count >= BatchSize)
                {
                    await FlushAsync(batch, stoppingToken);
                    nextFlush = DateTimeOffset.UtcNow + FlushInterval;
                    continue;
                }

                var remaining = nextFlush - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    if (batch.Count > 0)
                        await FlushAsync(batch, stoppingToken);

                    nextFlush = DateTimeOffset.UtcNow + FlushInterval;
                    continue;
                }

                using var waitCts =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                waitCts.CancelAfter(remaining);

                try
                {
                    if (!await queue.WaitToReadAsync(waitCts.Token))
                        break;

                    while (batch.Count < BatchSize &&
                           queue.TryRead(out var item))
                    {
                        if (item is not null)
                            batch.Add(item);
                    }
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested &&
                          waitCts.IsCancellationRequested)
                {
                    // Flush deadline reached. The next loop iteration performs
                    // the flush without leaving an orphaned channel waiter.
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

        try
        {
            await sink.WriteBatchAsync(snapshot, cancellationToken);
            batch.RemoveRange(0, snapshot.Length);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "telemetry_batch_failed count={Count}",
                snapshot.Length);

            // Preserve the batch for retry and avoid a hot failure loop.
            await Task.Delay(
                TimeSpan.FromSeconds(1),
                cancellationToken);
        }
    }
}
