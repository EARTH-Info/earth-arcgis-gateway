using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class UserCentricRateLimiterTests
{
    [Fact]
    public async Task AggregateBudgetSpansDifferentLayers()
    {
        var options = Options(burst: 10, refill: 1, resource: 100);
        var limiter = new InMemoryUserActivityRateLimiter(options);
        var activity = Activity(5);

        Assert.True((await limiter.ConsumeAsync(Key(0), activity, CancellationToken.None)).Allowed);
        Assert.True((await limiter.ConsumeAsync(Key(1), activity, CancellationToken.None)).Allowed);

        var denied = await limiter.ConsumeAsync(Key(2), Activity(1), CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal("aggregate_user_budget_exceeded", denied.ReasonCode);
    }

    [Fact]
    public async Task NormalMultiLayerPanBurstIsAllowedWithinInteractiveCapacity()
    {
        var limiter = new InMemoryUserActivityRateLimiter(
            Options(burst: 80, refill: 20, resource: 100));

        var decisions = new List<UserRateDecision>();
        for (var layer = 0; layer < 12; layer++)
        {
            decisions.Add(await limiter.ConsumeAsync(
                Key(layer),
                Activity(4),
                CancellationToken.None));
        }

        Assert.All(decisions, decision => Assert.True(decision.Allowed));
        Assert.Equal(32, decisions[^1].RemainingAggregateUnits);
    }

    [Fact]
    public async Task SustainedExtractionEventuallyThrottlesAggregateUser()
    {
        var limiter = new InMemoryUserActivityRateLimiter(
            Options(burst: 20, refill: 1, resource: 100));

        UserRateDecision? denied = null;
        for (var i = 0; i < 20; i++)
        {
            var decision = await limiter.ConsumeAsync(
                Key(i % 10),
                Activity(3),
                CancellationToken.None);
            if (!decision.Allowed)
            {
                denied = decision;
                break;
            }
        }

        Assert.NotNull(denied);
        Assert.Equal("aggregate_user_budget_exceeded", denied!.ReasonCode);
        Assert.True(denied.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task DifferentUsersHaveIndependentAggregateBudgets()
    {
        var limiter = new InMemoryUserActivityRateLimiter(
            Options(burst: 5, refill: 1, resource: 100));

        Assert.True((await limiter.ConsumeAsync(Key(0, "sub-a"), Activity(5), CancellationToken.None)).Allowed);
        Assert.False((await limiter.ConsumeAsync(Key(1, "sub-a"), Activity(1), CancellationToken.None)).Allowed);
        Assert.True((await limiter.ConsumeAsync(Key(0, "sub-b"), Activity(1), CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task IdleTimeRefillsBurstCapacity()
    {
        var limiter = new InMemoryUserActivityRateLimiter(
            Options(burst: 4, refill: 4, resource: 100));

        Assert.True((await limiter.ConsumeAsync(Key(0), Activity(4), CancellationToken.None)).Allowed);
        Assert.False((await limiter.ConsumeAsync(Key(0), Activity(1), CancellationToken.None)).Allowed);

        await Task.Delay(
            TimeSpan.FromMilliseconds(1100),
            TestContext.Current.CancellationToken);

        Assert.True((await limiter.ConsumeAsync(
            Key(1),
            Activity(4),
            TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task ResourceWindowCanThrottleOneLayerWithoutMultiplyingUserBudget()
    {
        var limiter = new InMemoryUserActivityRateLimiter(
            Options(burst: 100, refill: 100, resource: 3));

        Assert.True((await limiter.ConsumeAsync(Key(0), Activity(3), CancellationToken.None)).Allowed);
        var denied = await limiter.ConsumeAsync(Key(0), Activity(1), CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal("resource_budget_exceeded", denied.ReasonCode);

        Assert.True((await limiter.ConsumeAsync(Key(1), Activity(1), CancellationToken.None)).Allowed);
    }

    private static UserCentricRateLimitOptions Options(
        int burst,
        int refill,
        int resource) =>
        new()
        {
            AggregateBurstCapacity = burst,
            AggregateRefillUnitsPerSecond = refill,
            SustainedCapacity = Math.Max(100, burst),
            SustainedWindowSeconds = 60,
            DailyCapacity = Math.Max(1_000, burst),
            DailyWindowSeconds = 3600,
            ResourceCapacity = resource,
            ResourceWindowSeconds = 60
        };

    private static UserActivityRateKey Key(
        int layer,
        string subject = "sub-1") =>
        new(
            subject,
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
            units >= 6,
            false,
            null);
}
