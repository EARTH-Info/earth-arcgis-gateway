using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class TelemetryWorkerTests
{
    [Fact]
    public async Task SinkFailureDoesNotKillWorkerAndBatchIsRetried()
    {
        var queue = new TelemetryQueue();
        var sink = new FailOnceSink();
        var worker = new TelemetryWorker(
            queue,
            sink,
            NullLogger<TelemetryWorker>.Instance);

        for (var i = 0; i < 256; i++)
        {
            Assert.True(queue.TryWrite(new TelemetryEvent(
                DateTimeOffset.UtcNow,
                "sub-1",
                "tenant-1",
                "jtuwma",
                "Land/Parcels",
                "FeatureServer",
                0,
                "query",
                "GET",
                200,
                5,
                "ALLOW",
                "policy_allow",
                "v1",
                "cid-" + i)));
        }

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (sink.SuccessfulBatches == 0 &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(
                    50,
                    TestContext.Current.CancellationToken);
            }

            Assert.True(sink.Calls >= 2);
            Assert.Equal(1, sink.SuccessfulBatches);
            Assert.Equal(256, sink.LastBatchCount);
        }
        finally
        {
            await worker.StopAsync(
                TestContext.Current.CancellationToken);
        }
    }


    [Fact]
    public async Task PartialBatchFlushesWithinConfiguredInterval()
    {
        var queue = new TelemetryQueue();
        var sink = new CapturingSink();
        var worker = new TelemetryWorker(
            queue,
            sink,
            NullLogger<TelemetryWorker>.Instance);

        Assert.True(queue.TryWrite(new TelemetryEvent(
            DateTimeOffset.UtcNow,
            "sub-1",
            "tenant-1",
            "jtuwma",
            "Land/Parcels",
            "FeatureServer",
            0,
            "query",
            "GET",
            200,
            5,
            "ALLOW",
            "policy_allow",
            "v1",
            "cid-partial")));

        await worker.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (sink.Events.Count == 0 &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(
                    50,
                    TestContext.Current.CancellationToken);
            }

            Assert.Single(sink.Events);
            Assert.Equal("cid-partial", sink.Events[0].CorrelationId);
        }
        finally
        {
            await worker.StopAsync(
                TestContext.Current.CancellationToken);
        }
    }

    private sealed class CapturingSink : ITelemetrySink
    {
        public List<TelemetryEvent> Events { get; } = [];

        public Task WriteBatchAsync(
            IReadOnlyList<TelemetryEvent> events,
            CancellationToken cancellationToken)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceSink : ITelemetrySink
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public int SuccessfulBatches { get; private set; }
        public int LastBatchCount { get; private set; }

        public Task WriteBatchAsync(
            IReadOnlyList<TelemetryEvent> events,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
                throw new IOException("Simulated telemetry storage failure.");

            SuccessfulBatches++;
            LastBatchCount = events.Count;
            return Task.CompletedTask;
        }
    }
}
