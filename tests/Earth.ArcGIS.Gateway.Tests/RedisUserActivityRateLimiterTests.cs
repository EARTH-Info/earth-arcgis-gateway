using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class RedisUserActivityRateLimiterTests
{
    [Fact]
    public async Task ConcurrentRequestsAcrossLayersCannotOverspendAggregateBudget()
    {
        using var redis = await ConnectAsync();
        var limiter = Create(redis, burst: 20, refill: 1, resource: 100);
        var id = Guid.NewGuid().ToString("N");

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 40)
                .Select(i => limiter.ConsumeAsync(
                    Key(id, i),
                    Activity(1),
                    CancellationToken.None).AsTask()));

        Assert.Equal(20, decisions.Count(x => x.Allowed));
        Assert.Equal(20, decisions.Count(x => !x.Allowed));
        Assert.All(
            decisions.Where(x => !x.Allowed),
            x => Assert.Equal("aggregate_user_budget_exceeded", x.ReasonCode));
    }

    [Fact]
    public async Task SameNatConceptDoesNotCoupleDifferentEarthIdSubjects()
    {
        using var redis = await ConnectAsync();
        var limiter = Create(redis, burst: 3, refill: 1, resource: 100);
        var id = Guid.NewGuid().ToString("N");

        Assert.True((await limiter.ConsumeAsync(
            Key(id, 0, "sub-a"), Activity(3), CancellationToken.None)).Allowed);
        Assert.False((await limiter.ConsumeAsync(
            Key(id, 1, "sub-a"), Activity(1), CancellationToken.None)).Allowed);
        Assert.True((await limiter.ConsumeAsync(
            Key(id, 0, "sub-b"), Activity(1), CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task ResourceBudgetIsSecondaryToAggregateBudget()
    {
        using var redis = await ConnectAsync();
        var limiter = Create(redis, burst: 20, refill: 20, resource: 2);
        var id = Guid.NewGuid().ToString("N");

        Assert.True((await limiter.ConsumeAsync(
            Key(id, 0), Activity(2), CancellationToken.None)).Allowed);
        var layerDenied = await limiter.ConsumeAsync(
            Key(id, 0), Activity(1), CancellationToken.None);
        Assert.False(layerDenied.Allowed);
        Assert.Equal("resource_budget_exceeded", layerDenied.ReasonCode);

        Assert.True((await limiter.ConsumeAsync(
            Key(id, 1), Activity(1), CancellationToken.None)).Allowed);
    }

    private static RedisUserActivityRateLimiter Create(
        IConnectionMultiplexer redis,
        int burst,
        int refill,
        int resource)
    {
        var options = new UserCentricRateLimitOptions
        {
            ProfileVersion = "test",
            AggregateBurstCapacity = burst,
            AggregateRefillUnitsPerSecond = refill,
            SustainedCapacity = 1_000,
            SustainedWindowSeconds = 900,
            DailyCapacity = 10_000,
            DailyWindowSeconds = 86400,
            ResourceCapacity = resource,
            ResourceWindowSeconds = 60
        };

        return new RedisUserActivityRateLimiter(
            redis,
            options,
            NullLogger<RedisUserActivityRateLimiter>.Instance);
    }

    private static UserActivityRateKey Key(
        string id,
        int layer,
        string subject = "sub-1") =>
        new(
            subject + "-" + id,
            "tenant-1",
            "jtuwma",
            "Land/Parcels",
            layer,
            "query");

    private static RequestActivity Activity(int units) =>
        new(
            "query_geometry",
            units,
            false,
            true,
            false,
            false,
            false,
            null);

    private static Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(
            "localhost:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000");
}
