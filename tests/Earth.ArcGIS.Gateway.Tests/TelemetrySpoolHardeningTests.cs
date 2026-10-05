using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class TelemetrySpoolHardeningTests
{
    [Fact]
    public async Task ByteCapDropsOldestSpoolFile()
    {
        var directory = TempDirectory();
        try
        {
            var health = new TelemetryPersistenceHealth();
            var spool = CreateSpool(directory, health, maxBytes: 1);

            await spool.AppendAsync(
                [Event("cid-byte-cap")],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, spool.CountPendingFiles());
            Assert.Equal(0, spool.CountPendingBytes());
            Assert.Equal(1, health.DroppedSpoolBatches);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptSpoolFileIsQuarantinedAndDoesNotBlockReplay()
    {
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "000-corrupt.jsonl"),
                "{not-json\n",
                TestContext.Current.CancellationToken);

            var health = new TelemetryPersistenceHealth();
            var spool = CreateSpool(directory, health, maxBytes: 1024 * 1024);

            await spool.ReplayAsync(
                new CapturingSink(),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, spool.CountPendingFiles());
            Assert.Equal(1, health.QuarantinedSpoolFiles);
            var quarantine = Path.Combine(directory, "quarantine");
            Assert.True(Directory.Exists(quarantine));
            Assert.Single(Directory.EnumerateFiles(quarantine, "*.invalid.jsonl"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static FileTelemetrySpool CreateSpool(
        string directory,
        TelemetryPersistenceHealth health,
        long maxBytes) =>
        new(
            Options.Create(new TelemetryPersistenceOptions
            {
                SpoolDirectory = directory,
                MaxSpoolFiles = 10,
                MaxSpoolBytes = maxBytes,
                ReplayBatchFiles = 10
            }),
            health,
            NullLogger<FileTelemetrySpool>.Instance);

    private static TelemetryEvent Event(string correlationId) =>
        new(
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
            10,
            "ALLOW",
            "policy_allow",
            "v1",
            correlationId);

    private static string TempDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "earth-arcgis-gateway-tests",
            Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class CapturingSink : ITelemetrySink
    {
        public Task WriteBatchAsync(
            IReadOnlyList<TelemetryEvent> events,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
