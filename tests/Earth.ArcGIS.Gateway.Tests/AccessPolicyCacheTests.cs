using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AccessPolicyCacheTests
{
    [Fact]
    public async Task CacheSeparatesUsersResourcesAndOperations()
    {
        var cache = new InMemoryAccessPolicyCache();
        var decision = new AccessPolicyDecision(true, "allow", "v1", 30);

        await cache.SetAsync(
            Request("sub-a", 0, "query"),
            decision,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.NotNull(await cache.GetAsync(
            Request("sub-a", 0, "query"),
            CancellationToken.None));
        Assert.Null(await cache.GetAsync(
            Request("sub-b", 0, "query"),
            CancellationToken.None));
        Assert.Null(await cache.GetAsync(
            Request("sub-a", 1, "query"),
            CancellationToken.None));
        Assert.Null(await cache.GetAsync(
            Request("sub-a", 0, "metadata"),
            CancellationToken.None));
    }

    [Fact]
    public async Task ApplicationInvalidationRemovesOnlyThatApplicationsEntries()
    {
        var cache = new InMemoryAccessPolicyCache();
        var decision = new AccessPolicyDecision(true, "allow", "v1", 30);
        var jtuwma = Request("sub-a", 0, "query", "jtuwma");
        var geoforest = Request("sub-a", 0, "query", "geoforest");

        await cache.SetAsync(jtuwma, decision, TimeSpan.FromSeconds(30), CancellationToken.None);
        await cache.SetAsync(geoforest, decision, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(1, await cache.InvalidateApplicationAsync("jtuwma", CancellationToken.None));
        Assert.Null(await cache.GetAsync(jtuwma, CancellationToken.None));
        Assert.NotNull(await cache.GetAsync(geoforest, CancellationToken.None));
    }

    [Fact]
    public async Task CachingDecoratorCachesOnlyWhenAccessApiExplicitlyProvidesTtl()
    {
        var http = new HttpClient(new SequenceHandler(
            """{"allowed":true,"reasonCode":"allow","policyVersion":"v1","cacheTtlSeconds":30}""",
            """{"allowed":false,"reasonCode":"changed","policyVersion":"v2"}"""));
        var inner = new AccessPolicyClient(
            new StubFactory(http),
            NullLogger<AccessPolicyClient>.Instance);
        var cache = new InMemoryAccessPolicyCache();
        var client = new CachingAccessPolicyClient(
            inner,
            cache,
            NullLogger<CachingAccessPolicyClient>.Instance);
        var request = Request("sub-a", 0, "query");
        var app = new GatewayApplication("jtuwma", "https://policy.test/authorize");

        var first = await client.AuthorizeAsync(app, request, CancellationToken.None);
        var second = await client.AuthorizeAsync(app, request, CancellationToken.None);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal("v1", second.PolicyVersion);
    }

    [Fact]
    public async Task ZeroTtlNeverCaches()
    {
        var handler = new SequenceHandler(
            """{"allowed":true,"reasonCode":"allow","policyVersion":"v1","cacheTtlSeconds":0}""",
            """{"allowed":false,"reasonCode":"changed","policyVersion":"v2"}""");
        var inner = new AccessPolicyClient(
            new StubFactory(new HttpClient(handler)),
            NullLogger<AccessPolicyClient>.Instance);
        var client = new CachingAccessPolicyClient(
            inner,
            new InMemoryAccessPolicyCache(),
            NullLogger<CachingAccessPolicyClient>.Instance);
        var request = Request("sub-a", 0, "query");
        var app = new GatewayApplication("jtuwma", "https://policy.test/authorize");

        Assert.True((await client.AuthorizeAsync(app, request, CancellationToken.None)).Allowed);
        Assert.False((await client.AuthorizeAsync(app, request, CancellationToken.None)).Allowed);
        Assert.Equal(2, handler.RequestCount);
    }

    private static AccessPolicyRequest Request(
        string subject,
        int layer,
        string operation,
        string application = "jtuwma") =>
        new(
            subject,
            "tenant-1",
            application,
            "Land/Parcels",
            "FeatureServer",
            layer,
            operation,
            "GET",
            "cid");

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class SequenceHandler(params string[] responses)
        : HttpMessageHandler
    {
        private int index;
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = responses[Math.Min(index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
