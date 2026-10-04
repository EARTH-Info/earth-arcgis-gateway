using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public sealed class RedisRateLimitOptions
{
    public string? ConnectionString { get; set; }
}

public sealed class RedisGatewayRateLimiter : IGatewayRateLimiter
{
    private readonly IConnectionMultiplexer redis;
    private readonly GatewayRateLimitOptions options;
    private readonly ILogger<RedisGatewayRateLimiter> logger;
    private const string Script = """
local cost = tonumber(ARGV[1])
local capacities = { tonumber(ARGV[2]), tonumber(ARGV[4]), tonumber(ARGV[6]) }
local windows = { tonumber(ARGV[3]), tonumber(ARGV[5]), tonumber(ARGV[7]) }
local current = {}

for i = 1, 3 do
  local value = redis.call('GET', KEYS[i])
  if value then
    current[i] = tonumber(value)
  else
    current[i] = capacities[i]
  end

  if current[i] < cost then
    local ttl = redis.call('TTL', KEYS[i])
    if ttl < 1 then ttl = windows[i] end
    return {0, current[i], ttl, i}
  end
end

local remaining = {}
for i = 1, 3 do
  if redis.call('EXISTS', KEYS[i]) == 0 then
    remaining[i] = capacities[i] - cost
    redis.call('SET', KEYS[i], remaining[i], 'EX', windows[i])
  else
    remaining[i] = redis.call('DECRBY', KEYS[i], cost)
  end
end

local minimum = remaining[1]
if remaining[2] < minimum then minimum = remaining[2] end
if remaining[3] < minimum then minimum = remaining[3] end
return {1, minimum, 0, 0}
""";

    public RedisGatewayRateLimiter(
        IConnectionMultiplexer redis,
        GatewayRateLimitOptions options,
        ILogger<RedisGatewayRateLimiter> logger)
    {
        options.Validate();
        this.redis = redis;
        this.options = options;
        this.logger = logger;
    }

    public async ValueTask<RateLimitDecision> ConsumeAsync(
        RateLimitKey key,
        int units,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (units <= 0 || units > options.BurstCapacity)
        {
            return new(
                false,
                0,
                options.BurstWindowSeconds,
                "invalid_or_excessive_cost");
        }

        try
        {
            var db = redis.GetDatabase();
            var prefix = BuildKeyPrefix(key);
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                    Script,
                    [
                        $"{prefix}:burst",
                        $"{prefix}:sustained",
                        $"{prefix}:daily"
                    ],
                    [
                        units,
                        options.BurstCapacity,
                        options.BurstWindowSeconds,
                        options.SustainedCapacity,
                        options.SustainedWindowSeconds,
                        options.DailyCapacity,
                        options.DailyWindowSeconds
                    ])
                .WaitAsync(cancellationToken);

            if (result is null || result.Length != 4)
            {
                return new(
                    false,
                    0,
                    options.BurstWindowSeconds,
                    "rate_backend_malformed");
            }

            var allowed = (long)result[0] == 1;
            var remaining = checked((int)(long)result[1]);
            var retry = checked((int)(long)result[2]);
            var exhaustedWindow = checked((int)(long)result[3]);

            return new(
                allowed,
                remaining,
                retry,
                allowed ? "rate_allow" : exhaustedWindow switch
                {
                    1 => "burst_budget_exceeded",
                    2 => "sustained_budget_exceeded",
                    3 => "daily_budget_exceeded",
                    _ => "weighted_budget_exceeded"
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or InvalidCastException or OverflowException)
        {
            logger.LogError(ex, "redis_rate_limit_failure");
            return new(
                false,
                0,
                options.BurstWindowSeconds,
                "rate_backend_unavailable");
        }
    }

    private static string BuildKeyPrefix(RateLimitKey key)
    {
        var raw =
            $"{key.EarthIdSub}\n{key.Tenant ?? "-"}\n{key.Application}\n{key.Service}\n{key.LayerId?.ToString() ?? "-"}\n{key.Operation}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"eiag:rate:{hash}";
    }
}
