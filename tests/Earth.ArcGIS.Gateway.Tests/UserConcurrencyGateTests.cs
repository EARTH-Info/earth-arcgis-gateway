using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class UserConcurrencyGateTests
{
    [Fact]
    public async Task InMemoryGateEnforcesTotalAndHeavyLimitsPerUser()
    {
        var options = new UserConcurrencyOptions
        {
            InteractiveLimit = 2,
            HeavyLimit = 1,
            LeaseSeconds = 90
        };
        var gate = new InMemoryUserConcurrencyGate(options);

        var heavy = await gate.TryEnterAsync(Key("sub-a"), true, CancellationToken.None);
        var interactive = await gate.TryEnterAsync(Key("sub-a"), false, CancellationToken.None);
        var totalDenied = await gate.TryEnterAsync(Key("sub-a"), false, CancellationToken.None);
        var secondHeavyDenied = await gate.TryEnterAsync(Key("sub-a"), true, CancellationToken.None);
        var otherUser = await gate.TryEnterAsync(Key("sub-b"), false, CancellationToken.None);

        Assert.NotNull(heavy);
        Assert.NotNull(interactive);
        Assert.Null(totalDenied);
        Assert.Null(secondHeavyDenied);
        Assert.NotNull(otherUser);

        await heavy!.DisposeAsync();
        var recovered = await gate.TryEnterAsync(Key("sub-a"), true, CancellationToken.None);
        Assert.NotNull(recovered);

        await interactive!.DisposeAsync();
        await otherUser!.DisposeAsync();
        await recovered!.DisposeAsync();
    }

    [Fact]
    public async Task RedisGateIsAtomicAcrossInstancesAndHeavyCountsTowardTotal()
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

        var heavy = await first.TryEnterAsync(key, true, CancellationToken.None);
        var interactive = await second.TryEnterAsync(key, false, CancellationToken.None);
        var totalDenied = await first.TryEnterAsync(key, false, CancellationToken.None);
        var secondHeavyDenied = await second.TryEnterAsync(key, true, CancellationToken.None);

        Assert.NotNull(heavy);
        Assert.NotNull(interactive);
        Assert.Null(totalDenied);
        Assert.Null(secondHeavyDenied);

        await heavy!.DisposeAsync();
        var recoveredHeavy = await second.TryEnterAsync(key, true, CancellationToken.None);
        Assert.NotNull(recoveredHeavy);

        await interactive!.DisposeAsync();
        await recoveredHeavy!.DisposeAsync();
    }

    [Fact]
    public async Task LeaseReleaseIsIdempotent()
    {
        var options = new UserConcurrencyOptions
        {
            InteractiveLimit = 1,
            HeavyLimit = 1,
            LeaseSeconds = 90
        };
        var gate = new InMemoryUserConcurrencyGate(options);
        var key = Key("sub-idempotent");

        var lease = await gate.TryEnterAsync(key, true, CancellationToken.None);
        Assert.NotNull(lease);

        await lease!.DisposeAsync();
        await lease.DisposeAsync();

        var next = await gate.TryEnterAsync(key, true, CancellationToken.None);
        Assert.NotNull(next);
        await next!.DisposeAsync();
    }

    private static UserConcurrencyKey Key(string subject) =>
        new(subject, "tenant-1", "jtuwma");

    private static Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(
            "localhost:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000");
}
