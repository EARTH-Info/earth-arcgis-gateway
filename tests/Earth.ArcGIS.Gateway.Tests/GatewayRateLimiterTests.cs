using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class GatewayRateLimiterTests
{
    [Fact]
    public async Task WeightedUnitsAreConsumed()
    {
        var limiter = new InMemoryGatewayRateLimiter();
        var key = new RateLimitKey("sub-1", "jtuwma", "Land/Parcels", 0);

        var first = await limiter.ConsumeAsync(key, 5, CancellationToken.None);
        var second = await limiter.ConsumeAsync(key, 5, CancellationToken.None);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal(first.RemainingUnits - 5, second.RemainingUnits);
    }

    [Fact]
    public async Task ExhaustedBudgetReturns429CompatibleDecision()
    {
        var limiter = new InMemoryGatewayRateLimiter();
        var key = new RateLimitKey("sub-1", "jtuwma", "Land/Parcels", 0);

        for (var i = 0; i < 48; i++)
            Assert.True((await limiter.ConsumeAsync(key, 5, CancellationToken.None)).Allowed);

        var denied = await limiter.ConsumeAsync(key, 1, CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task DifferentUsersHaveIndependentBudgets()
    {
        var limiter = new InMemoryGatewayRateLimiter();
        var resource = ("jtuwma", "Land/Parcels", (int?)0);

        var a = new RateLimitKey("sub-a", resource.Item1, resource.Item2, resource.Item3);
        var b = new RateLimitKey("sub-b", resource.Item1, resource.Item2, resource.Item3);

        Assert.True((await limiter.ConsumeAsync(a, 240, CancellationToken.None)).Allowed);
        Assert.False((await limiter.ConsumeAsync(a, 1, CancellationToken.None)).Allowed);
        Assert.True((await limiter.ConsumeAsync(b, 1, CancellationToken.None)).Allowed);
    }
}
