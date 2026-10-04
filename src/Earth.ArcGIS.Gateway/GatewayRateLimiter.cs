using System.Collections.Concurrent;

namespace Earth.ArcGIS.Gateway;

public sealed record RateLimitKey(string EarthIdSub, string Application, string Service, int? LayerId);
public sealed record RateLimitDecision(bool Allowed, int RemainingUnits, int RetryAfterSeconds, string ReasonCode);

public interface IGatewayRateLimiter
{
    ValueTask<RateLimitDecision> ConsumeAsync(RateLimitKey key, int units, CancellationToken cancellationToken);
}

public sealed class InMemoryGatewayRateLimiter : IGatewayRateLimiter
{
    private const int Capacity = 240;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<RateLimitKey, Counter> _counters = new();

    public ValueTask<RateLimitDecision> ConsumeAsync(RateLimitKey key, int units, CancellationToken cancellationToken)
    {
        if (units <= 0 || units > Capacity)
            return ValueTask.FromResult(new RateLimitDecision(false, 0, 60, "invalid_or_excessive_cost"));

        var now = DateTimeOffset.UtcNow;
        var counter = _counters.GetOrAdd(key, _ => new Counter(now, Capacity));

        lock (counter)
        {
            if (now - counter.WindowStart >= Window)
            {
                counter.WindowStart = now;
                counter.Remaining = Capacity;
            }

            if (counter.Remaining < units)
            {
                var retry = Math.Max(1, (int)Math.Ceiling((Window - (now - counter.WindowStart)).TotalSeconds));
                return ValueTask.FromResult(new RateLimitDecision(false, counter.Remaining, retry, "weighted_budget_exceeded"));
            }

            counter.Remaining -= units;
            return ValueTask.FromResult(new RateLimitDecision(true, counter.Remaining, 0, "rate_allow"));
        }
    }

    private sealed class Counter(DateTimeOffset windowStart, int remaining)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;
        public int Remaining { get; set; } = remaining;
    }
}
