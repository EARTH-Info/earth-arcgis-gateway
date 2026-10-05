using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class UserConcurrencyGateTests
{
    [Fact]
    public async Task InMemoryGateIsolatesUsersAndHeavyClass()
    {
        var options = new UserConcurrencyOptions
        {
            InteractiveLimit = 2,
            HeavyLimit = 1,
            LeaseSeconds = 90
        };
        var gate = new InMemoryUserConcurrencyGate(options);

        var a1 = await gate.TryEnterAsync(Key("sub-a"), false, CancellationToken.None);
        var a2 = await gate.TryEnterAsync(Key("sub-a"), false, CancellationToken.None);
        var a3 = await gate.TryEnterAsync(Key("sub-a"), false, CancellationToken.None);
        var b1 = await gate.TryEnterAsync(Key("sub-b"), false, CancellationToken.None);
        var heavy1 = await gate.TryEnterAsync(Key("sub-a"), true, CancellationToken.None);
        var heavy2 = await gate.TryEnterAsync(Key("sub-a"), true, CancellationToken.None);

        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.Null(a3);
        Assert.NotNull(b1);
        Assert.NotNull(heavy1);
        Assert.Null(heavy2);

        await a1!.DisposeAsync();
        await a2!.DisposeAsync();
        await b1!.DisposeAsync();
        await heavy1!.DisposeAsync();
    }

    [Fact]
    public async Task RedisGateIsAtomicAcrossInstancesAndReleases()
    {
        using var redis = await ConnectAsync();
        var options = new UserConcurrencyOptions
        {
            InteractiveLimit = 2,
            HeavyLimit = 1,
            LeaseSeconds = 90
        };
        var first = new RedisUserConcurrencyGate(
            redis,
            options,
            NullLogger<RedisUserConcurrencyGate>.Instance);
        var second = new RedisUserConcurrencyGate(
            redis,
            options,
            NullLogger<RedisUserConcurrencyGate>.Instance);
        var key = Key("sub-" + Guid.NewGuid().ToString("N"));

        var one = await first.TryEnterAsync(key, false, CancellationToken.None);
        var two = await second.TryEnterAsync(key, false, CancellationToken.None);
        var denied = await first.TryEnterAsync(key, false, CancellationToken.None);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Null(denied);

        await one!.DisposeAsync();
        var recovered = await second.TryEnterAsync(key, false, CancellationToken.None);
        Assert.NotNull(recovered);

        await two!.DisposeAsync();
        await recovered!.DisposeAsync();
    }

    private static UserConcurrencyKey Key(string subject) =>
        new(subject, "tenant-1", "jtuwma");

    private static Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(
            "localhost:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000");
}
