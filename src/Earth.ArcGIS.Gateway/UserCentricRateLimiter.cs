using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public sealed class UserCentricRateLimitOptions
{
    public string ProfileVersion { get; set; } = "interactive-v1-provisional";
    public int AggregateBurstCapacity { get; set; } = 400;
    public int AggregateRefillUnitsPerSecond { get; set; } = 20;
    public int SustainedCapacity { get; set; } = 6_000;
    public int SustainedWindowSeconds { get; set; } = 15 * 60;
    public int DailyCapacity { get; set; } = 50_000;
    public int DailyWindowSeconds { get; set; } = 24 * 60 * 60;
    public int ResourceCapacity { get; set; } = 600;
    public int ResourceWindowSeconds { get; set; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileVersion) ||
            AggregateBurstCapacity <= 0 ||
            AggregateRefillUnitsPerSecond <= 0 ||
            SustainedCapacity <= 0 ||
            SustainedWindowSeconds <= 0 ||
            DailyCapacity <= 0 ||
            DailyWindowSeconds <= 0 ||
            ResourceCapacity <= 0 ||
            ResourceWindowSeconds <= 0)
        {
            throw new InvalidOperationException("User-centric rate-limit configuration is invalid.");
        }

        if (SustainedCapacity < AggregateBurstCapacity ||
            DailyCapacity < SustainedCapacity)
        {
            throw new InvalidOperationException(
                "Sustained/daily capacities must be at least as large as the aggregate burst capacity.");
        }
    }
}

public sealed record UserActivityRateKey(
    string EarthIdSub,
    string? Tenant,
    string Application,
    string Service,
    int? LayerId,
    string Operation);

public sealed record UserRateDecision(
    bool Allowed,
    int RemainingAggregateUnits,
    int? RemainingResourceUnits,
    int RetryAfterSeconds,
    string ReasonCode,
    string ProfileVersion);

public interface IUserActivityRateLimiter
{
    ValueTask<UserRateDecision> ConsumeAsync(
        UserActivityRateKey key,
        RequestActivity activity,
        CancellationToken cancellationToken);
}

public sealed class InMemoryUserActivityRateLimiter : IUserActivityRateLimiter
{
    private readonly UserCentricRateLimitOptions options;
    private readonly ConcurrentDictionary<UserKey, UserState> users = new();

    public InMemoryUserActivityRateLimiter(UserCentricRateLimitOptions options)
    {
        options.Validate();
        this.options = options;
    }

    public ValueTask<UserRateDecision> ConsumeAsync(
        UserActivityRateKey key,
        RequestActivity activity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var units = activity.CostUnits;
        if (units <= 0 || units > options.AggregateBurstCapacity)
            return ValueTask.FromResult(Deny("invalid_or_excessive_cost", options.ResourceWindowSeconds));

        var now = DateTimeOffset.UtcNow;
        var userKey = new UserKey(key.EarthIdSub, key.Tenant, key.Application);
        var state = users.GetOrAdd(userKey, _ => new UserState(now, options));

        lock (state)
        {
            state.Refill(now, options);
            state.Sustained.ResetIfExpired(now, options.SustainedCapacity, options.SustainedWindowSeconds);
            state.Daily.ResetIfExpired(now, options.DailyCapacity, options.DailyWindowSeconds);

            var resourceKey = new ResourceKey(key.Service, key.LayerId, key.Operation);
            var resource = state.Resources.GetValueOrDefault(resourceKey);
            if (resource is null)
            {
                resource = new WindowCounter(now, options.ResourceCapacity);
                state.Resources[resourceKey] = resource;
            }
            resource.ResetIfExpired(now, options.ResourceCapacity, options.ResourceWindowSeconds);

            if (state.Tokens + 0.000001 < units)
            {
                var retry = Math.Max(1, (int)Math.Ceiling(
                    (units - state.Tokens) / options.AggregateRefillUnitsPerSecond));
                return ValueTask.FromResult(Deny(
                    "aggregate_user_budget_exceeded",
                    retry,
                    (int)Math.Floor(state.Tokens),
                    resource.Remaining));
            }

            if (state.Sustained.Remaining < units)
                return ValueTask.FromResult(DenyWindow("sustained_user_budget_exceeded", state.Sustained, now, resource.Remaining));

            if (state.Daily.Remaining < units)
                return ValueTask.FromResult(DenyWindow("daily_user_budget_exceeded", state.Daily, now, resource.Remaining));

            if (resource.Remaining < units)
                return ValueTask.FromResult(DenyWindow("resource_budget_exceeded", resource, now, resource.Remaining));

            state.Tokens -= units;
            state.Sustained.Remaining -= units;
            state.Daily.Remaining -= units;
            resource.Remaining -= units;

            state.CleanupExpiredResources(now, options.ResourceWindowSeconds);

            return ValueTask.FromResult(new UserRateDecision(
                true,
                Math.Max(0, (int)Math.Floor(state.Tokens)),
                Math.Max(0, resource.Remaining),
                0,
                "rate_allow",
                options.ProfileVersion));
        }
    }

    private UserRateDecision DenyWindow(
        string reason,
        WindowCounter counter,
        DateTimeOffset now,
        int resourceRemaining) =>
        Deny(reason, counter.RetryAfterSeconds(now), counter.Remaining, resourceRemaining);

    private UserRateDecision Deny(
        string reason,
        int retry,
        int remaining = 0,
        int? resourceRemaining = null) =>
        new(false, Math.Max(0, remaining), resourceRemaining, Math.Max(1, retry), reason, options.ProfileVersion);

    private sealed record UserKey(string Subject, string? Tenant, string Application);
    private sealed record ResourceKey(string Service, int? LayerId, string Operation);

    private sealed class UserState
    {
        public UserState(DateTimeOffset now, UserCentricRateLimitOptions options)
        {
            Tokens = options.AggregateBurstCapacity;
            LastRefill = now;
            Sustained = new WindowCounter(now, options.SustainedCapacity);
            Daily = new WindowCounter(now, options.DailyCapacity);
        }

        public double Tokens { get; set; }
        public DateTimeOffset LastRefill { get; set; }
        public WindowCounter Sustained { get; }
        public WindowCounter Daily { get; }
        public Dictionary<ResourceKey, WindowCounter> Resources { get; } = [];

        public void Refill(DateTimeOffset now, UserCentricRateLimitOptions options)
        {
            var seconds = Math.Max(0, (now - LastRefill).TotalSeconds);
            Tokens = Math.Min(
                options.AggregateBurstCapacity,
                Tokens + seconds * options.AggregateRefillUnitsPerSecond);
            LastRefill = now;
        }

        public void CleanupExpiredResources(DateTimeOffset now, int windowSeconds)
        {
            if (Resources.Count < 512)
                return;

            var expired = Resources
                .Where(x => now - x.Value.WindowStart >= TimeSpan.FromSeconds(windowSeconds * 2L))
                .Select(x => x.Key)
                .Take(256)
                .ToArray();
            foreach (var key in expired)
                Resources.Remove(key);
        }
    }

    private sealed class WindowCounter(DateTimeOffset start, int remaining)
    {
        public DateTimeOffset WindowStart { get; set; } = start;
        public int Remaining { get; set; } = remaining;
        public int WindowSeconds { get; set; }

        public void ResetIfExpired(DateTimeOffset now, int capacity, int windowSeconds)
        {
            WindowSeconds = windowSeconds;
            if (now - WindowStart < TimeSpan.FromSeconds(windowSeconds))
                return;
            WindowStart = now;
            Remaining = capacity;
        }

        public int RetryAfterSeconds(DateTimeOffset now)
        {
            var remaining = TimeSpan.FromSeconds(WindowSeconds) - (now - WindowStart);
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }
}

public sealed class RedisUserActivityRateLimiter : IUserActivityRateLimiter
{
    private readonly IConnectionMultiplexer redis;
    private readonly UserCentricRateLimitOptions options;
    private readonly ILogger<RedisUserActivityRateLimiter> logger;

    private const string Script = """
local cost = tonumber(ARGV[1])
local burst = tonumber(ARGV[2])
local refill = tonumber(ARGV[3])
local sustainedCapacity = tonumber(ARGV[4])
local sustainedWindow = tonumber(ARGV[5])
local dailyCapacity = tonumber(ARGV[6])
local dailyWindow = tonumber(ARGV[7])
local resourceCapacity = tonumber(ARGV[8])
local resourceWindow = tonumber(ARGV[9])
local aggregateTtl = tonumber(ARGV[10])

local nowParts = redis.call('TIME')
local nowMs = tonumber(nowParts[1]) * 1000 + math.floor(tonumber(nowParts[2]) / 1000)

local tokens = tonumber(redis.call('HGET', KEYS[1], 'tokens'))
local lastMs = tonumber(redis.call('HGET', KEYS[1], 'lastMs'))
if not tokens then tokens = burst end
if not lastMs then lastMs = nowMs end

local elapsed = math.max(0, nowMs - lastMs) / 1000.0
tokens = math.min(burst, tokens + elapsed * refill)

local sustained = tonumber(redis.call('GET', KEYS[2]))
if not sustained then sustained = sustainedCapacity end
local daily = tonumber(redis.call('GET', KEYS[3]))
if not daily then daily = dailyCapacity end
local resource = tonumber(redis.call('GET', KEYS[4]))
if not resource then resource = resourceCapacity end

redis.call('HSET', KEYS[1], 'tokens', tokens, 'lastMs', nowMs)
redis.call('EXPIRE', KEYS[1], aggregateTtl)

if tokens < cost then
  local retry = math.ceil((cost - tokens) / refill)
  return {0, math.floor(tokens), resource, math.max(1, retry), 1}
end
if sustained < cost then
  local ttl = redis.call('TTL', KEYS[2])
  if ttl < 1 then ttl = sustainedWindow end
  return {0, math.floor(tokens), resource, ttl, 2}
end
if daily < cost then
  local ttl = redis.call('TTL', KEYS[3])
  if ttl < 1 then ttl = dailyWindow end
  return {0, math.floor(tokens), resource, ttl, 3}
end
if resource < cost then
  local ttl = redis.call('TTL', KEYS[4])
  if ttl < 1 then ttl = resourceWindow end
  return {0, math.floor(tokens), resource, ttl, 4}
end

tokens = tokens - cost
redis.call('HSET', KEYS[1], 'tokens', tokens, 'lastMs', nowMs)

if redis.call('EXISTS', KEYS[2]) == 0 then
  sustained = sustainedCapacity - cost
  redis.call('SET', KEYS[2], sustained, 'EX', sustainedWindow)
else
  sustained = redis.call('DECRBY', KEYS[2], cost)
end

if redis.call('EXISTS', KEYS[3]) == 0 then
  daily = dailyCapacity - cost
  redis.call('SET', KEYS[3], daily, 'EX', dailyWindow)
else
  daily = redis.call('DECRBY', KEYS[3], cost)
end

if redis.call('EXISTS', KEYS[4]) == 0 then
  resource = resourceCapacity - cost
  redis.call('SET', KEYS[4], resource, 'EX', resourceWindow)
else
  resource = redis.call('DECRBY', KEYS[4], cost)
end

return {1, math.floor(tokens), resource, 0, 0}
""";

    public RedisUserActivityRateLimiter(
        IConnectionMultiplexer redis,
        UserCentricRateLimitOptions options,
        ILogger<RedisUserActivityRateLimiter> logger)
    {
        options.Validate();
        this.redis = redis;
        this.options = options;
        this.logger = logger;
    }

    public async ValueTask<UserRateDecision> ConsumeAsync(
        UserActivityRateKey key,
        RequestActivity activity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var units = activity.CostUnits;
        if (units <= 0 || units > options.AggregateBurstCapacity)
            return Deny("invalid_or_excessive_cost", options.ResourceWindowSeconds);

        try
        {
            var db = redis.GetDatabase();
            var aggregatePrefix = BuildAggregatePrefix(key);
            var resourcePrefix = BuildResourcePrefix(key);
            var stateTtl = Math.Max(
                options.DailyWindowSeconds,
                (int)Math.Ceiling((double)options.AggregateBurstCapacity / options.AggregateRefillUnitsPerSecond) * 2);

            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                    Script,
                    [
                        $"{aggregatePrefix}:bucket",
                        $"{aggregatePrefix}:sustained",
                        $"{aggregatePrefix}:daily",
                        $"{resourcePrefix}:window"
                    ],
                    [
                        units,
                        options.AggregateBurstCapacity,
                        options.AggregateRefillUnitsPerSecond,
                        options.SustainedCapacity,
                        options.SustainedWindowSeconds,
                        options.DailyCapacity,
                        options.DailyWindowSeconds,
                        options.ResourceCapacity,
                        options.ResourceWindowSeconds,
                        stateTtl
                    ])
                .WaitAsync(cancellationToken);

            if (result is null || result.Length != 5)
                return Deny("rate_backend_malformed", options.ResourceWindowSeconds);

            var allowed = (long)result[0] == 1;
            var aggregateRemaining = checked((int)(long)result[1]);
            var resourceRemaining = checked((int)(long)result[2]);
            var retry = checked((int)(long)result[3]);
            var reasonIndex = checked((int)(long)result[4]);

            return new UserRateDecision(
                allowed,
                Math.Max(0, aggregateRemaining),
                Math.Max(0, resourceRemaining),
                Math.Max(0, retry),
                allowed ? "rate_allow" : reasonIndex switch
                {
                    1 => "aggregate_user_budget_exceeded",
                    2 => "sustained_user_budget_exceeded",
                    3 => "daily_user_budget_exceeded",
                    4 => "resource_budget_exceeded",
                    _ => "weighted_budget_exceeded"
                },
                options.ProfileVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or InvalidCastException or OverflowException)
        {
            logger.LogError(ex, "redis_user_rate_limit_failure");
            return Deny("rate_backend_unavailable", options.ResourceWindowSeconds);
        }
    }

    private UserRateDecision Deny(string reason, int retry) =>
        new(false, 0, null, Math.Max(1, retry), reason, options.ProfileVersion);

    private static string BuildAggregatePrefix(UserActivityRateKey key)
    {
        var raw = $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}";
        return $"eiag:user-rate:{Hash(raw)}";
    }

    private static string BuildResourcePrefix(UserActivityRateKey key)
    {
        var raw = $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}\n{key.Service}\n{key.LayerId?.ToString() ?? "-"}\n{key.Operation}";
        return $"eiag:resource-rate:{Hash(raw)}";
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
