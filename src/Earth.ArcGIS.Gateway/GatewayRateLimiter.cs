using System.Collections.Concurrent;

namespace Earth.ArcGIS.Gateway;

public sealed record RateLimitKey(
    string EarthIdSub,
    string? Tenant,
    string Application,
    string Service,
    int? LayerId,
    string Operation);

public sealed record RateLimitDecision(
    bool Allowed,
    int RemainingUnits,
    int RetryAfterSeconds,
    string ReasonCode);

public sealed class GatewayRateLimitOptions
{
    public int BurstCapacity { get; set; } = 240;
    public int BurstWindowSeconds { get; set; } = 60;
    public int SustainedCapacity { get; set; } = 1_800;
    public int SustainedWindowSeconds { get; set; } = 15 * 60;
    public int DailyCapacity { get; set; } = 20_000;
    public int DailyWindowSeconds { get; set; } = 24 * 60 * 60;

    public void Validate()
    {
        if (BurstCapacity <= 0 || SustainedCapacity <= 0 || DailyCapacity <= 0 ||
            BurstWindowSeconds <= 0 || SustainedWindowSeconds <= 0 || DailyWindowSeconds <= 0)
        {
            throw new InvalidOperationException("All rate-limit capacities and windows must be positive.");
        }

        if (SustainedCapacity < BurstCapacity || DailyCapacity < SustainedCapacity)
            throw new InvalidOperationException("Rate-limit capacities must increase from burst to sustained to daily.");
    }
}

public interface IGatewayRateLimiter
{
    ValueTask<RateLimitDecision> ConsumeAsync(
        RateLimitKey key,
        int units,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGatewayRateLimiter : IGatewayRateLimiter
{
    private readonly GatewayRateLimitOptions options;
    private readonly ConcurrentDictionary<RateLimitKey, CounterSet> counters = new();

    public InMemoryGatewayRateLimiter(GatewayRateLimitOptions? options = null)
    {
        this.options = options ?? new GatewayRateLimitOptions();
        this.options.Validate();
    }

    public ValueTask<RateLimitDecision> ConsumeAsync(
        RateLimitKey key,
        int units,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (units <= 0 || units > options.BurstCapacity)
        {
            return ValueTask.FromResult(new RateLimitDecision(
                false,
                0,
                options.BurstWindowSeconds,
                "invalid_or_excessive_cost"));
        }

        var now = DateTimeOffset.UtcNow;
        var set = counters.GetOrAdd(key, _ => CounterSet.Create(now, options));

        lock (set)
        {
            set.Burst.ResetIfExpired(now, options.BurstCapacity, options.BurstWindowSeconds);
            set.Sustained.ResetIfExpired(now, options.SustainedCapacity, options.SustainedWindowSeconds);
            set.Daily.ResetIfExpired(now, options.DailyCapacity, options.DailyWindowSeconds);

            var exhausted = FindExhausted(set, units, now);
            if (exhausted is not null)
            {
                return ValueTask.FromResult(new RateLimitDecision(
                    false,
                    Math.Max(0, exhausted.Remaining),
                    exhausted.RetryAfterSeconds(now),
                    exhausted.ReasonCode));
            }

            set.Burst.Remaining -= units;
            set.Sustained.Remaining -= units;
            set.Daily.Remaining -= units;

            return ValueTask.FromResult(new RateLimitDecision(
                true,
                Math.Min(set.Burst.Remaining, Math.Min(set.Sustained.Remaining, set.Daily.Remaining)),
                0,
                "rate_allow"));
        }
    }

    private ExhaustedWindow? FindExhausted(CounterSet set, int units, DateTimeOffset now)
    {
        if (set.Burst.Remaining < units)
            return new(set.Burst.Remaining, set.Burst.WindowStart, options.BurstWindowSeconds, "burst_budget_exceeded");

        if (set.Sustained.Remaining < units)
            return new(set.Sustained.Remaining, set.Sustained.WindowStart, options.SustainedWindowSeconds, "sustained_budget_exceeded");

        if (set.Daily.Remaining < units)
            return new(set.Daily.Remaining, set.Daily.WindowStart, options.DailyWindowSeconds, "daily_budget_exceeded");

        return null;
    }

    private sealed class CounterSet
    {
        public required WindowCounter Burst { get; init; }
        public required WindowCounter Sustained { get; init; }
        public required WindowCounter Daily { get; init; }

        public static CounterSet Create(DateTimeOffset now, GatewayRateLimitOptions options) =>
            new()
            {
                Burst = new(now, options.BurstCapacity),
                Sustained = new(now, options.SustainedCapacity),
                Daily = new(now, options.DailyCapacity)
            };
    }

    private sealed class WindowCounter(DateTimeOffset windowStart, int remaining)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;
        public int Remaining { get; set; } = remaining;

        public void ResetIfExpired(DateTimeOffset now, int capacity, int windowSeconds)
        {
            if (now - WindowStart < TimeSpan.FromSeconds(windowSeconds))
                return;

            WindowStart = now;
            Remaining = capacity;
        }
    }

    private sealed record ExhaustedWindow(
        int Remaining,
        DateTimeOffset WindowStart,
        int WindowSeconds,
        string ReasonCode)
    {
        public int RetryAfterSeconds(DateTimeOffset now)
        {
            var remaining = TimeSpan.FromSeconds(WindowSeconds) - (now - WindowStart);
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }
}
