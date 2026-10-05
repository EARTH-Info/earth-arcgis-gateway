using System.Net;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AuditQueryStoreTests
{
    [Fact]
    public async Task RecentStoreFiltersUserLayerAndDecision()
    {
        var queue = new TelemetryQueue();
        queue.TryWrite(Event("sub-a", 0, "ALLOW", "cid-a"));
        queue.TryWrite(Event("sub-a", 1, "DENY", "cid-b"));
        queue.TryWrite(Event("sub-b", 1, "DENY", "cid-c"));

        var store = new RecentAuditQueryStore(queue);
        var result = await store.QueryAsync(
            new AuditQuery(
                EarthIdSub: "sub-a",
                LayerId: 1,
                Decision: "DENY",
                Limit: 10),
            CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal("cid-b", item.CorrelationId);
    }

    [Fact]
    public async Task ClickHouseStoreUsesBoundParametersAndDeserializesRows()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            JsonLine(Event("sub-a", 1, "ALLOW", "cid-1")));
        var options = Options.Create(new TelemetryPersistenceOptions
        {
            ClickHouseBaseUrl = "https://clickhouse.test",
            Database = "gis",
            Table = "gateway_telemetry",
            Username = "gateway",
            Password = "secret"
        });
        var store = new ClickHouseAuditQueryStore(
            new StubFactory(new HttpClient(handler)),
            options,
            NullLogger<ClickHouseAuditQueryStore>.Instance);

        var result = await store.QueryAsync(
            new AuditQuery(
                EarthIdSub: "sub-a' OR 1=1 --",
                Application: "jtuwma",
                LayerId: 1,
                Limit: 25),
            CancellationToken.None);

        Assert.Single(result);
        Assert.NotNull(handler.Uri);
        var url = handler.Uri!.ToString();
        Assert.Contains("param_sub=", url, StringComparison.Ordinal);
        Assert.Contains("param_application=jtuwma", url, StringComparison.Ordinal);
        Assert.DoesNotContain("earthIdSub%20%3D%20%27sub-a", url, StringComparison.Ordinal);
        Assert.Equal("gateway", handler.UserHeader);
        Assert.Equal("secret", handler.KeyHeader);
    }

    [Fact]
    public async Task ClickHouseStoreRejectsOversizedResponse()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            new string('x', 4 * 1024 * 1024 + 1));
        var store = new ClickHouseAuditQueryStore(
            new StubFactory(new HttpClient(handler)),
            Options.Create(new TelemetryPersistenceOptions
            {
                ClickHouseBaseUrl = "https://clickhouse.test",
                Database = "default",
                Table = "gateway_telemetry"
            }),
            NullLogger<ClickHouseAuditQueryStore>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.QueryAsync(
                new AuditQuery(Limit: 1),
                TestContext.Current.CancellationToken));
    }

    private static TelemetryEvent Event(
        string subject,
        int layer,
        string decision,
        string correlation) =>
        new(
            DateTimeOffset.UtcNow,
            subject,
            "tenant-1",
            "jtuwma",
            "Land/Parcels",
            "FeatureServer",
            layer,
            "query",
            "GET",
            decision == "ALLOW" ? 200 : 403,
            10,
            decision,
            decision == "ALLOW" ? "policy_allow" : "policy_deny",
            "v1",
            correlation,
            ResponseBytes: 100,
            RateLimitRemaining: 50,
            ActivityClass: "query_geometry",
            RateCostUnits: 3,
            ResourceRateRemaining: 100,
            RateProfileVersion: "test",
            ConcurrencyClass: "interactive",
            IsSpatial: false,
            ReturnsGeometry: true,
            IsPaged: false,
            IsHeavy: false,
            IsExtractionLike: false,
            QueryFingerprint: "ABC");

    private static string JsonLine(TelemetryEvent item) =>
        System.Text.Json.JsonSerializer.Serialize(
            item,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            }) + "\n";

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? UserHeader { get; private set; }
        public string? KeyHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            UserHeader = request.Headers.TryGetValues("X-ClickHouse-User", out var users)
                ? users.Single()
                : null;
            KeyHeader = request.Headers.TryGetValues("X-ClickHouse-Key", out var keys)
                ? keys.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
            });
        }
    }
}
