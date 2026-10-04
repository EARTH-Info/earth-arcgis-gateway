using System.Net;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class TelemetryPersistenceTests
{
    [Fact]
    public async Task FileSpoolPersistsAndReplaysBatch()
    {
        var directory = TempDirectory();
        try
        {
            var health = new TelemetryPersistenceHealth();
            var spool = CreateSpool(directory, health);
            var destination = new CapturingSink();
            var batch = new[] { Event("cid-1"), Event("cid-2") };

            await spool.AppendAsync(batch, CancellationToken.None);

            Assert.Equal(1, spool.CountPendingFiles());
            Assert.Equal(1, health.SpooledBatches);

            await spool.ReplayAsync(destination, CancellationToken.None);

            Assert.Equal(0, spool.CountPendingFiles());
            Assert.Equal(1, health.ReplayedBatches);
            Assert.Equal(2, destination.Events.Count);
            Assert.Equal(["cid-1", "cid-2"], destination.Events.Select(x => x.CorrelationId).ToArray());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SpoolCapacityIsBoundedAndObservable()
    {
        var directory = TempDirectory();
        try
        {
            var health = new TelemetryPersistenceHealth();
            var spool = CreateSpool(directory, health, maxFiles: 1);

            await spool.AppendAsync([Event("cid-1")], CancellationToken.None);
            await Task.Delay(5);
            await spool.AppendAsync([Event("cid-2")], CancellationToken.None);

            Assert.Equal(1, spool.CountPendingFiles());
            Assert.Equal(1, health.DroppedSpoolBatches);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ClickHouseFailureFallsBackToSpoolWithoutThrowing()
    {
        var directory = TempDirectory();
        try
        {
            var options = Options.Create(new TelemetryPersistenceOptions
            {
                ClickHouseBaseUrl = "https://clickhouse.test",
                Database = "default",
                Table = "gateway_telemetry",
                SpoolDirectory = directory,
                MaxSpoolFiles = 10
            });
            var health = new TelemetryPersistenceHealth();
            var primary = new ClickHouseTelemetrySink(
                new StubHttpClientFactory(new HttpClient(new FailingHandler())),
                options);
            var spool = new FileTelemetrySpool(
                options,
                health,
                NullLogger<FileTelemetrySpool>.Instance);
            var resilient = new ResilientTelemetrySink(
                primary,
                spool,
                health,
                NullLogger<ResilientTelemetrySink>.Instance);

            await resilient.WriteBatchAsync([Event("cid-fail")], CancellationToken.None);

            Assert.Equal(1, health.StorageFailures);
            Assert.Equal(1, health.SpooledBatches);
            Assert.Equal(1, spool.CountPendingFiles());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ClickHouseSinkUsesJsonEachRowInsert()
    {
        var handler = new RecordingHandler();
        var options = Options.Create(new TelemetryPersistenceOptions
        {
            ClickHouseBaseUrl = "https://clickhouse.test",
            Database = "gis",
            Table = "gateway_telemetry",
            Username = "gateway",
            Password = "secret"
        });
        var sink = new ClickHouseTelemetrySink(
            new StubHttpClientFactory(new HttpClient(handler)),
            options);

        await sink.WriteBatchAsync([Event("cid-1")], CancellationToken.None);

        Assert.NotNull(handler.Uri);
        Assert.Contains("database=gis", handler.Uri!.Query, StringComparison.Ordinal);
        Assert.Contains("INSERT", Uri.UnescapeDataString(handler.Uri.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", handler.Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains(""correlationId":"cid-1"", handler.Body, StringComparison.Ordinal);
        Assert.Equal("gateway", handler.UserHeader);
        Assert.Equal("secret", handler.KeyHeader);
    }

    private static FileTelemetrySpool CreateSpool(
        string directory,
        TelemetryPersistenceHealth health,
        int maxFiles = 10)
    {
        var options = Options.Create(new TelemetryPersistenceOptions
        {
            SpoolDirectory = directory,
            MaxSpoolFiles = maxFiles,
            ReplayBatchFiles = 10
        });

        return new FileTelemetrySpool(
            options,
            health,
            NullLogger<FileTelemetrySpool>.Instance);
    }

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
        public List<TelemetryEvent> Events { get; } = [];

        public Task WriteBatchAsync(
            IReadOnlyList<TelemetryEvent> events,
            CancellationToken cancellationToken)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("ClickHouse unavailable.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? UserHeader { get; private set; }
        public string? KeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            UserHeader = request.Headers.TryGetValues("X-ClickHouse-User", out var users)
                ? users.Single()
                : null;
            KeyHeader = request.Headers.TryGetValues("X-ClickHouse-Key", out var keys)
                ? keys.Single()
                : null;

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
