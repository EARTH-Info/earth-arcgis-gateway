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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();

    public InMemoryUserConcurrencyGate(UserConcurrencyOptions options)
    {
        this.options = options;
    }

    public async ValueTask<IUserConcurrencyLease?> TryEnterAsync(
        UserConcurrencyKey key,
        bool heavy,
        CancellationToken cancellationToken)
    {
        var concurrencyClass = heavy ? "heavy" : "interactive";
        var limit = heavy ? options.HeavyLimit : options.InteractiveLimit;
        var identity = BuildIdentity(key, concurrencyClass);
        var gate = gates.GetOrAdd(identity, _ => new SemaphoreSlim(limit, limit));

        if (!await gate.WaitAsync(0, cancellationToken))
            return null;

        return new InMemoryLease(gate, concurrencyClass);
    }

    private static string BuildIdentity(UserConcurrencyKey key, string concurrencyClass) =>
        $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}\n{concurrencyClass}";

    private sealed class InMemoryLease(
        SemaphoreSlim gate,
        string concurrencyClass) : IUserConcurrencyLease
    {
        private int released;
        public string Class { get; } = concurrencyClass;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                gate.Release();

            return ValueTask.CompletedTask;
        }
    }
}

public sealed class RedisUserConcurrencyGate : IUserConcurrencyGate
{
    private const string AcquireScript = """
local now = tonumber(ARGV[1])
local expires = tonumber(ARGV[2])
local limit = tonumber(ARGV[3])
local leaseId = ARGV[4]

redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
local current = redis.call('ZCARD', KEYS[1])
if current >= limit then
  return {0, current}
end

redis.call('ZADD', KEYS[1], expires, leaseId)
redis.call('PEXPIRE', KEYS[1], math.max(1000, expires - now + 5000))
return {1, current + 1}
""";

    private const string ReleaseScript = """
return redis.call('ZREM', KEYS[1], ARGV[1])
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
        var limit = heavy ? options.HeavyLimit : options.InteractiveLimit;
        var redisKey = BuildRedisKey(key, concurrencyClass);
        var leaseId = Guid.NewGuid().ToString("N");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresMs = nowMs + (options.LeaseSeconds * 1000L);

        try
        {
            var db = redis.GetDatabase();
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                    AcquireScript,
                    [redisKey],
                    [nowMs, expiresMs, limit, leaseId])
                .WaitAsync(cancellationToken);

            if (result is null || result.Length != 2)
                throw new RedisException("Concurrency script returned a malformed result.");

            if ((long)result[0] != 1)
                return null;

            return new RedisLease(db, redisKey, leaseId, concurrencyClass, logger);
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

    private static RedisKey BuildRedisKey(UserConcurrencyKey key, string concurrencyClass)
    {
        var raw = $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}\n{concurrencyClass}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"eiag:concurrency:{hash}";
    }

    private sealed class RedisLease(
        IDatabase database,
        RedisKey key,
        string leaseId,
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
                    [key],
                    [leaseId]);
            }
            catch (RedisException ex)
            {
                logger.LogWarning(ex, "redis_user_concurrency_release_failed class={Class}", Class);
            }
        }
    }
}
