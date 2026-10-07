using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public sealed class UserConcurrencyOptions
{
    public int InteractiveLimit { get; set; } = 12;
    public int HeavyLimit { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 90;

    public void Validate(int gatewayRequestTimeoutSeconds)
    {
        if (InteractiveLimit <= 0 || HeavyLimit <= 0 || LeaseSeconds <= 0)
            throw new InvalidOperationException("User concurrency limits must be positive.");

        if (HeavyLimit > InteractiveLimit)
            throw new InvalidOperationException("Heavy concurrency limit cannot exceed interactive limit.");

        if (LeaseSeconds <= gatewayRequestTimeoutSeconds)
        {
            throw new InvalidOperationException(
                "UserConcurrency:LeaseSeconds must exceed the gateway request timeout so live requests do not lose their lease.");
        }
    }
}

public sealed record UserConcurrencyKey(
    string EarthIdSub,
    string? Tenant,
    string Application);

public interface IUserConcurrencyLease : IAsyncDisposable
{
    string Class { get; }
}

public interface IUserConcurrencyGate
{
    ValueTask<IUserConcurrencyLease?> TryEnterAsync(
        UserConcurrencyKey key,
        bool heavy,
        CancellationToken cancellationToken);
}

public sealed class InMemoryUserConcurrencyGate : IUserConcurrencyGate
{
    private readonly UserConcurrencyOptions options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> totalGates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> heavyGates = new();

    public InMemoryUserConcurrencyGate(UserConcurrencyOptions options)
    {
        this.options = options;
    }

    public async ValueTask<IUserConcurrencyLease?> TryEnterAsync(
        UserConcurrencyKey key,
        bool heavy,
        CancellationToken cancellationToken)
    {
        var identity = BuildIdentity(key);
        var totalGate = totalGates.GetOrAdd(
            identity,
            _ => new SemaphoreSlim(options.InteractiveLimit, options.InteractiveLimit));

        if (!await totalGate.WaitAsync(0, cancellationToken))
            return null;

        if (!heavy)
            return new InMemoryLease(totalGate, null, "interactive");

        var heavyGate = heavyGates.GetOrAdd(
            identity,
            _ => new SemaphoreSlim(options.HeavyLimit, options.HeavyLimit));

        try
        {
            if (!await heavyGate.WaitAsync(0, cancellationToken))
            {
                totalGate.Release();
                return null;
            }

            return new InMemoryLease(totalGate, heavyGate, "heavy");
        }
        catch
        {
            totalGate.Release();
            throw;
        }
    }

    private static string BuildIdentity(UserConcurrencyKey key) =>
        $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}";

    private sealed class InMemoryLease(
        SemaphoreSlim totalGate,
        SemaphoreSlim? heavyGate,
        string concurrencyClass) : IUserConcurrencyLease
    {
        private int released;
        public string Class { get; } = concurrencyClass;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return ValueTask.CompletedTask;

            heavyGate?.Release();
            totalGate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class RedisUserConcurrencyGate : IUserConcurrencyGate
{
    private const string AcquireScript = """
local leaseMs = tonumber(ARGV[1])
local totalLimit = tonumber(ARGV[2])
local heavyLimit = tonumber(ARGV[3])
local leaseId = ARGV[4]
local isHeavy = tonumber(ARGV[5])

local nowParts = redis.call('TIME')
local now = tonumber(nowParts[1]) * 1000 + math.floor(tonumber(nowParts[2]) / 1000)
local expires = now + leaseMs

redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
local totalCurrent = redis.call('ZCARD', KEYS[1])
if totalCurrent >= totalLimit then
  return {0, totalCurrent, -1}
end

local heavyCurrent = 0
if isHeavy == 1 then
  redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
  heavyCurrent = redis.call('ZCARD', KEYS[2])
  if heavyCurrent >= heavyLimit then
    return {0, totalCurrent, heavyCurrent}
  end
end

redis.call('ZADD', KEYS[1], expires, leaseId)
redis.call('PEXPIRE', KEYS[1], math.max(1000, leaseMs + 5000))

if isHeavy == 1 then
  redis.call('ZADD', KEYS[2], expires, leaseId)
  redis.call('PEXPIRE', KEYS[2], math.max(1000, leaseMs + 5000))
  heavyCurrent = heavyCurrent + 1
end

return {1, totalCurrent + 1, heavyCurrent}
""";

    private const string ReleaseScript = """
local removed = redis.call('ZREM', KEYS[1], ARGV[1])
if tonumber(ARGV[2]) == 1 then
  redis.call('ZREM', KEYS[2], ARGV[1])
end
return removed
""";

    private readonly IConnectionMultiplexer redis;
    private readonly UserConcurrencyOptions options;
    private readonly ILogger<RedisUserConcurrencyGate> logger;

    public RedisUserConcurrencyGate(
        IConnectionMultiplexer redis,
        UserConcurrencyOptions options,
        ILogger<RedisUserConcurrencyGate> logger)
    {
        this.redis = redis;
        this.options = options;
        this.logger = logger;
    }

    public async ValueTask<IUserConcurrencyLease?> TryEnterAsync(
        UserConcurrencyKey key,
        bool heavy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var concurrencyClass = heavy ? "heavy" : "interactive";
        var totalKey = BuildRedisKey(key, "total");
        var heavyKey = BuildRedisKey(key, "heavy");
        var leaseId = Guid.NewGuid().ToString("N");

        try
        {
            var db = redis.GetDatabase();
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                    AcquireScript,
                    [totalKey, heavyKey],
                    [
                        options.LeaseSeconds * 1000L,
                        options.InteractiveLimit,
                        options.HeavyLimit,
                        leaseId,
                        heavy ? 1 : 0
                    ])
                .WaitAsync(cancellationToken);

            if (result is null || result.Length != 3)
                throw new RedisException("Concurrency script returned a malformed result.");

            if ((long)result[0] != 1)
                return null;

            return new RedisLease(
                db,
                totalKey,
                heavyKey,
                leaseId,
                heavy,
                concurrencyClass,
                logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisException ex)
        {
            logger.LogError(ex, "redis_user_concurrency_failure class={Class}", concurrencyClass);
            throw new InvalidOperationException("Distributed user concurrency backend is unavailable.", ex);
        }
    }

    private static RedisKey BuildRedisKey(UserConcurrencyKey key, string scope)
    {
        var raw = $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}\n{scope}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"eiag:concurrency:{hash}";
    }

    private sealed class RedisLease(
        IDatabase database,
        RedisKey totalKey,
        RedisKey heavyKey,
        string leaseId,
        bool heavy,
        string concurrencyClass,
        ILogger logger) : IUserConcurrencyLease
    {
        private int released;
        public string Class { get; } = concurrencyClass;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;

            try
            {
                await database.ScriptEvaluateAsync(
                    ReleaseScript,
                    [totalKey, heavyKey],
                    [leaseId, heavy ? 1 : 0]);
            }
            catch (RedisException ex)
            {
                logger.LogWarning(ex, "redis_user_concurrency_release_failed class={Class}", Class);
            }
        }
    }
}
