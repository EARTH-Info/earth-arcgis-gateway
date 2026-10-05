using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ClickHouseIntegrationTests
{
    [Fact]
    public async Task TelemetryInsertAndDurableAuditQueryRoundTrip()
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8123"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var factory = new StubFactory(http);
        var options = Options.Create(new TelemetryPersistenceOptions
        {
            ClickHouseBaseUrl = "http://localhost:8123",
            Database = "default",
            Table = "gateway_telemetry",
            AdminAuditTable = "gateway_admin_audit"
        });
        var correlation = "ci-" + Guid.NewGuid().ToString("N");
        var sink = new ClickHouseTelemetrySink(factory, options);
        var query = new ClickHouseAuditQueryStore(
            factory,
            options,
            NullLogger<ClickHouseAuditQueryStore>.Instance);

        var expected = new TelemetryEvent(
            DateTimeOffset.UtcNow,
            "sub-clickhouse",
            "tenant-1",
            "jtuwma",
            "Land/Parcels",
            "FeatureServer",
            0,
            "query",
            "GET",
            200,
            12,
            "ALLOW",
            "policy_allow",
            "v1",
            correlation,
            ResponseBytes: 456,
            RateLimitRemaining: 321,
            ActivityClass: "query_geometry",
            RateCostUnits: 3,
            ResourceRateRemaining: 200,
            RateProfileVersion: "test-v1",
            ConcurrencyClass: "interactive",
            IsSpatial: false,
            ReturnsGeometry: true,
            IsPaged: false,
            IsHeavy: false,
            IsExtractionLike: false,
            QueryFingerprint: "ABC123",
            RecordCount: 5);

        await sink.WriteBatchAsync(
            [expected],
            TestContext.Current.CancellationToken);

        var rows = await query.QueryAsync(
            new AuditQuery(CorrelationId: correlation, Limit: 10),
            TestContext.Current.CancellationToken);

        var actual = Assert.Single(rows);
        Assert.Equal(expected.EarthIdSub, actual.EarthIdSub);
        Assert.Equal(expected.LayerId, actual.LayerId);
        Assert.Equal(expected.ActivityClass, actual.ActivityClass);
        Assert.Equal(expected.RateCostUnits, actual.RateCostUnits);
        Assert.Equal(expected.RecordCount, actual.RecordCount);
    }

    [Fact]
    public async Task AdminAuditInsertAndQueryRoundTrip()
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8123"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var factory = new StubFactory(http);
        var options = Options.Create(new TelemetryPersistenceOptions
        {
            ClickHouseBaseUrl = "http://localhost:8123",
            Database = "default",
            AdminAuditTable = "gateway_admin_audit"
        });
        var store = new ClickHouseAdminAuditStore(
            factory,
            options,
            NullLogger<ClickHouseAdminAuditStore>.Instance);
        var correlation = "admin-ci-" + Guid.NewGuid().ToString("N");
        var expected = new AdminEnforcementEvent(
            DateTimeOffset.UtcNow,
            "admin-sub",
            "BLOCK",
            "target-sub-" + Guid.NewGuid().ToString("N"),
            "integration-test",
            DateTimeOffset.UtcNow.AddMinutes(5),
            correlation);

        await store.AddAsync(
            expected,
            TestContext.Current.CancellationToken);

        var rows = await store.GetRecentAsync(
            1000,
            TestContext.Current.CancellationToken);

        var actual = Assert.Single(rows.Where(x => x.CorrelationId == correlation));
        Assert.Equal(expected.AdminSubject, actual.AdminSubject);
        Assert.Equal(expected.TargetSubject, actual.TargetSubject);
        Assert.Equal(expected.Action, actual.Action);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
