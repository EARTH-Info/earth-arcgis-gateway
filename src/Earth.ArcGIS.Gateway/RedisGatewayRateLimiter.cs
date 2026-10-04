using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public sealed class RedisRateLimitOptions
{
    public string? ConnectionString { get; set; }
    public int Capacity { get; set; } = 240;
    public int WindowSeconds { get; set; } = 60;
}

public sealed class RedisGatewayRateLimiter(
    IConnectionMultiplexer redis,
    RedisRateLimitOptions options,
    ILogger<RedisGatewayRateLimiter> logger) : IGatewayRateLimiter
{
    private const string Script = """
local current = redis.call('GET', KEYS[1])
if not current then
  current = tonumber(ARGV[1])
else
  current = tonumber(current)
end
local cost = tonumber(ARGV[2])
if current < cost then
  local ttl = redis.call('TTL', KEYS[1])
  if ttl < 1 then ttl = tonumber(ARGV[3]) end
  return {0, current, ttl}
end
local remaining = current - cost
redis.call('SET', KEYS[1], remaining, 'EX', ARGV[3])
return {1, remaining, 0}
""";

    public async ValueTask<RateLimitDecision> ConsumeAsync(
        RateLimitKey key, int units, CancellationToken cancellationToken)
    {
        if (units <= 0 || units > options.Capacity)
            return new(false, 0, options.WindowSeconds, "invalid_or_excessive_cost");

        try
        {
            var db = redis.GetDatabase();
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                Script,
                [BuildKey(key)],
                [options.Capacity, units, options.WindowSeconds]);

            if (result is null || result.Length != 3)
                return new(false, 0, options.WindowSeconds, "rate_backend_malformed");

            var allowed = (long)result[0] == 1;
            var remaining = checked((int)(long)result[1]);
            var retry = checked((int)(long)result[2]);
            return new(allowed, remaining, retry, allowed ? "rate_allow" : "weighted_budget_exceeded");
        }
        catch (RedisException ex)
        {
            logger.LogError(ex, "redis_rate_limit_failure");
            return new(false, 0, options.WindowSeconds, "rate_backend_unavailable");
        }
    }

    private static RedisKey BuildKey(RateLimitKey key)
    {
        var raw = $"{key.EarthIdSub}\n{key.Application}\n{key.Service}\n{key.LayerId?.ToString() ?? "-"}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"eiag:rate:{hash}";
    }
}
