using System.Net;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AdminAuditPersistenceTests
{
    [Fact]
    public async Task ClickHouseAdminAuditWritesAndReadsWithoutSecretInUri()
    {
        var item = new AdminEnforcementEvent(
            DateTimeOffset.UtcNow,
            "admin-sub",
            "BLOCK",
            "target-sub",
            "abuse",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "cid-1");
        var handler = new AdminAuditHandler(item);
        var store = Create(handler);

        await store.AddAsync(item, TestContext.Current.CancellationToken);
        var result = await store.GetRecentAsync(
            10,
            TestContext.Current.CancellationToken);

        var observed = Assert.Single(result);
        Assert.Equal(item.TargetSubject, observed.TargetSubject);
        Assert.Equal(item.Action, observed.Action);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.DoesNotContain("secret", request.Uri.ToString(), StringComparison.Ordinal));
        Assert.Contains("gateway_admin_audit", Uri.UnescapeDataString(handler.Requests[0].Uri.Query), StringComparison.Ordinal);
        Assert.Contains("param_limit=10", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InMemoryAdminAuditStoreRemainsAvailableForDevelopment()
    {
        IAdminAuditStore store = new AdminAuditStore();
        var item = new AdminEnforcementEvent(
            DateTimeOffset.UtcNow,
            "admin",
            "UNBLOCK",
            "target",
            "manual_unblock",
            null,
            "cid-dev");

        await store.AddAsync(item, CancellationToken.None);
        var result = await store.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal("cid-dev", Assert.Single(result).CorrelationId);
    }

    private static ClickHouseAdminAuditStore Create(HttpMessageHandler handler) =>
        new(
            new StubFactory(new HttpClient(handler)),
            Options.Create(new TelemetryPersistenceOptions
            {
                ClickHouseBaseUrl = "https://clickhouse.test",
                Database = "gis",
                AdminAuditTable = "gateway_admin_audit",
                Username = "gateway",
                Password = "secret"
            }),
            NullLogger<ClickHouseAdminAuditStore>.Instance);

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class AdminAuditHandler(AdminEnforcementEvent responseEvent)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, body));

            var responseBody = request.Method == HttpMethod.Get
                ? System.Text.Json.JsonSerializer.Serialize(
                    responseEvent,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    }) + "\n"
                : "";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/x-ndjson")
            };
        }
    }

    private sealed record RecordedRequest(Uri Uri, string? Body);
}
