using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class RedisGatewayRateLimiterTests
{
    [Fact]
    public async Task ConcurrentRequestsCannotOverspendBurstBudget()
    {
        using var redis = await ConnectAsync();
        var limiter = Create(redis, burstCapacity: 20, burstWindowSeconds: 30);
        var key = Key("concurrency");

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 40)
                .Select(_ => limiter.ConsumeAsync(key, 1, CancellationToken.None).AsTask()));

        Assert.Equal(20, decisions.Count(x => x.Allowed));
        Assert.Equal(20, decisions.Count(x => !x.Allowed));
        Assert.All(decisions.Where(x => !x.Allowed),
            x => Assert.Equal("burst_budget_exceeded", x.ReasonCode));
    }

    [Fact]
    public async Task DifferentOperationsUseIndependentBudgets()
    {
        using var redis = await ConnectAsync();
        var limiter = Create(redis, burstCapacity: 2, burstWindowSeconds: 30);
        var id = Guid.NewGuid().ToString("N");
        var query = Key(id, "query");
        var metadata = Key(id, "metadata");

        Assert.True((await limiter.ConsumeAsync(query, 2, CancellationToken.None)).Allowed);
        Assert.False((await limiter.ConsumeAsync(query, 1, CancellationToken.None)).Allowed);
        Assert.True((await limiter.ConsumeAsync(metadata, 1, CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task FixedWindowExpiryIsNotExtendedByLaterRequests()
    {
        using var redis = await ConnectAsync();
        var limiter = Create(redis, burstCapacity: 2, burstWindowSeconds: 3);
        var key = Key("fixed-" + Guid.NewGuid().ToString("N"));

        Assert.True((await limiter.ConsumeAsync(key, 1, CancellationToken.None)).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.True((await limiter.ConsumeAsync(key, 1, CancellationToken.None)).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(1700));

        var afterOriginalWindow = await limiter.ConsumeAsync(key, 1, CancellationToken.None);
        Assert.True(afterOriginalWindow.Allowed);
    }

    [Fact]
    public async Task SustainedBudgetIsEnforcedAfterBurstWindows()
    {
        using var redis = await ConnectAsync();
        var options = new GatewayRateLimitOptions
        {
            BurstCapacity = 10,
            BurstWindowSeconds = 1,
            SustainedCapacity = 3,
            SustainedWindowSeconds = 30,
            DailyCapacity = 100,
            DailyWindowSeconds = 3600
        };
        var limiter = new RedisGatewayRateLimiter(
            redis,
            options,
            NullLogger<RedisGatewayRateLimiter>.Instance);
        var key = Key("sustained-" + Guid.NewGuid().ToString("N"));

        Assert.True((await limiter.ConsumeAsync(key, 3, CancellationToken.None)).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var denied = await limiter.ConsumeAsync(key, 1, CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal("sustained_budget_exceeded", denied.ReasonCode);
    }

    private static RedisGatewayRateLimiter Create(
        IConnectionMultiplexer redis,
        int burstCapacity,
        int burstWindowSeconds)
    {
        var options = new GatewayRateLimitOptions
        {
            BurstCapacity = burstCapacity,
            BurstWindowSeconds = burstWindowSeconds,
            SustainedCapacity = 1_000,
            SustainedWindowSeconds = 900,
            DailyCapacity = 10_000,
            DailyWindowSeconds = 86400
        };

        return new RedisGatewayRateLimiter(
            redis,
            options,
            NullLogger<RedisGatewayRateLimiter>.Instance);
    }

    private static RateLimitKey Key(string id, string operation = "query") =>
        new(
            "sub-" + id,
            "tenant-1",
            "jtuwma",
            "Land/Parcels",
            0,
            operation);

    private static Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(
            "localhost:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000");
}
