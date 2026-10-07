using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AccessPolicyCacheTests
{
    [Fact]
    public async Task CacheSeparatesUsersResourcesAndOperations()
    {
        var cache = new InMemoryAccessPolicyCache();
        var decision = new AccessPolicyDecision(true, "allow", "v1", 30);
        var request = Request("sub-a", 0, "query");
        var lookup = await cache.GetAsync(request, CancellationToken.None);

        await cache.SetAsync(
            request,
            decision,
            TimeSpan.FromSeconds(30),
            lookup.Generation,
            CancellationToken.None);

        Assert.NotNull((await cache.GetAsync(
            request,
            CancellationToken.None)).Decision);
        Assert.Null((await cache.GetAsync(
            Request("sub-b", 0, "query"),
            CancellationToken.None)).Decision);
        Assert.Null((await cache.GetAsync(
            Request("sub-a", 1, "query"),
            CancellationToken.None)).Decision);
        Assert.Null((await cache.GetAsync(
            Request("sub-a", 0, "metadata"),
            CancellationToken.None)).Decision);
    }

    [Fact]
    public async Task ApplicationInvalidationAdvancesGenerationAndRejectsStaleWrite()
    {
        var cache = new InMemoryAccessPolicyCache();
        var decision = new AccessPolicyDecision(true, "allow", "v1", 30);
        var request = Request("sub-a", 0, "query", "jtuwma");
        var before = await cache.GetAsync(request, CancellationToken.None);

        await cache.SetAsync(
            request,
            decision,
            TimeSpan.FromSeconds(30),
            before.Generation,
            CancellationToken.None);
        Assert.NotNull((await cache.GetAsync(request, CancellationToken.None)).Decision);

        var generation = await cache.InvalidateApplicationAsync(
            "jtuwma",
            CancellationToken.None);
        Assert.True(generation > before.Generation);
        Assert.Null((await cache.GetAsync(request, CancellationToken.None)).Decision);

        await cache.SetAsync(
            request,
            decision,
            TimeSpan.FromSeconds(30),
            before.Generation,
            CancellationToken.None);
        Assert.Null((await cache.GetAsync(request, CancellationToken.None)).Decision);
    }

    [Fact]
    public async Task RedisInvalidationIsVisibleAcrossInstancesAndStaleWriteCannotRestoreAllow()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(
            "localhost:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000");
        var first = new RedisAccessPolicyCache(
            redis,
            NullLogger<RedisAccessPolicyCache>.Instance);
        var second = new RedisAccessPolicyCache(
            redis,
            NullLogger<RedisAccessPolicyCache>.Instance);
        var app = "jtuwma-" + Guid.NewGuid().ToString("N");
        var request = Request("sub-a", 0, "query", app);
        var decision = new AccessPolicyDecision(true, "allow", "v1", 30);
        var lookup = await first.GetAsync(request, TestContext.Current.CancellationToken);

        await first.SetAsync(
            request,
            decision,
            TimeSpan.FromSeconds(30),
            lookup.Generation,
            TestContext.Current.CancellationToken);
        Assert.True((await second.GetAsync(
            request,
            TestContext.Current.CancellationToken)).Decision?.Allowed);

        var generation = await second.InvalidateApplicationAsync(
            app,
            TestContext.Current.CancellationToken);
        Assert.True(generation > lookup.Generation);
        Assert.Null((await first.GetAsync(
            request,
            TestContext.Current.CancellationToken)).Decision);

        await first.SetAsync(
            request,
            decision,
            TimeSpan.FromSeconds(30),
            lookup.Generation,
            TestContext.Current.CancellationToken);
        Assert.Null((await second.GetAsync(
            request,
            TestContext.Current.CancellationToken)).Decision);
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
