using Earth.ArcGIS.Gateway;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class RedisUserBlockStoreTests
{
    [Fact]
    public async Task BlockIsVisibleAcrossStoreInstancesAndCanBeRemoved()
    {
        using var redis = await ConnectAsync();
        var first = new RedisUserBlockStore(redis);
        var second = new RedisUserBlockStore(redis);
        var subject = "sub-" + Guid.NewGuid().ToString("N");

        var block = await first.BlockAsync(
            subject,
            "abuse",
            "admin-sub",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var observed = await second.GetActiveAsync(
            subject,
            TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal(block.EarthIdSub, observed.EarthIdSub);
        Assert.Equal("abuse", observed.Reason);
        Assert.Contains(
            await second.GetActiveAsync(TestContext.Current.CancellationToken),
            x => x.EarthIdSub == subject);

        Assert.True(await second.UnblockAsync(
            subject,
            TestContext.Current.CancellationToken));

        Assert.Null(await first.GetActiveAsync(
            subject,
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            await first.GetActiveAsync(TestContext.Current.CancellationToken),
            x => x.EarthIdSub == subject);
    }

    [Fact]
    public async Task TemporaryBlockExpiresWithoutManualCleanup()
    {
        using var redis = await ConnectAsync();
        var store = new RedisUserBlockStore(redis);
        var subject = "sub-exp-" + Guid.NewGuid().ToString("N");

        await store.BlockAsync(
            subject,
            "temporary",
            "admin-sub",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(await store.GetActiveAsync(
            subject,
            TestContext.Current.CancellationToken));

        await Task.Delay(
            TimeSpan.FromMilliseconds(1200),
            TestContext.Current.CancellationToken);

        Assert.Null(await store.GetActiveAsync(
            subject,
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            await store.GetActiveAsync(TestContext.Current.CancellationToken),
            x => x.EarthIdSub == subject);
    }

    private static Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(
            "localhost:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000");
}
