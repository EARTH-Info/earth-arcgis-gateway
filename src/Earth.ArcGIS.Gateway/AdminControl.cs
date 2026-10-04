using System.Collections.Concurrent;
using System.Security.Claims;

namespace Earth.ArcGIS.Gateway;

public sealed class AdminOptions
{
    public string[] AllowedSubjects { get; set; } = Array.Empty<string>();
    public string[] AllowedRoles { get; set; } = ["gateway-admin"];
    public int RecentTelemetryLimit { get; set; } = 500;
}

public static class AdminAuthorization
{
    public static bool IsAuthorized(ClaimsPrincipal principal, AdminOptions options)
    {
        var subject = principal.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(subject) &&
            options.AllowedSubjects.Contains(subject, StringComparer.Ordinal))
            return true;

        var roles = principal.FindAll("role")
            .Concat(principal.FindAll("roles"))
            .SelectMany(claim => claim.Value.Split(
                [' ', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return roles.Any(role =>
            options.AllowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
    }
}

public interface IConnectionMultiplexerAccessor
{
    bool Configured { get; }
    bool IsConnected { get; }
}

public sealed class ConnectionMultiplexerAccessor(IServiceProvider services)
    : IConnectionMultiplexerAccessor
{
    public bool Configured =>
        services.GetService<StackExchange.Redis.IConnectionMultiplexer>() is not null;

    public bool IsConnected =>
        services.GetService<StackExchange.Redis.IConnectionMultiplexer>()?.IsConnected ?? false;
}

public sealed record UserBlock(
    string EarthIdSub,
    string Reason,
    string AdminSubject,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    public bool IsActive(DateTimeOffset now) =>
        ExpiresAt is null || ExpiresAt > now;
}

public sealed record AdminEnforcementEvent(
    DateTimeOffset Timestamp,
    string AdminSubject,
    string Action,
    string TargetSubject,
    string Reason,
    DateTimeOffset? ExpiresAt,
    string CorrelationId);

public interface IUserBlockStore
{
    ValueTask<UserBlock?> GetActiveAsync(
        string earthIdSub,
        CancellationToken cancellationToken);

    ValueTask<UserBlock> BlockAsync(
        string earthIdSub,
        string reason,
        string adminSubject,
        TimeSpan? duration,
        CancellationToken cancellationToken);

    ValueTask<bool> UnblockAsync(
        string earthIdSub,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<UserBlock>> GetActiveAsync(
        CancellationToken cancellationToken);
}

public sealed class UserBlockStore : IUserBlockStore
{
    private readonly ConcurrentDictionary<string, UserBlock> blocks =
        new(StringComparer.Ordinal);

    public ValueTask<UserBlock?> GetActiveAsync(
        string earthIdSub,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (blocks.TryGetValue(earthIdSub, out var current))
        {
            if (current.IsActive(DateTimeOffset.UtcNow))
                return ValueTask.FromResult<UserBlock?>(current);

            blocks.TryRemove(earthIdSub, out _);
        }

        return ValueTask.FromResult<UserBlock?>(null);
    }

    public ValueTask<UserBlock> BlockAsync(
        string earthIdSub,
        string reason,
        string adminSubject,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var block = CreateBlock(
            earthIdSub,
            reason,
            adminSubject,
            duration);

        blocks[block.EarthIdSub] = block;
        return ValueTask.FromResult(block);
    }

    public ValueTask<bool> UnblockAsync(
        string earthIdSub,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(blocks.TryRemove(earthIdSub, out _));
    }

    public ValueTask<IReadOnlyList<UserBlock>> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in blocks)
        {
            if (!pair.Value.IsActive(now))
                blocks.TryRemove(pair.Key, out _);
        }

        return ValueTask.FromResult<IReadOnlyList<UserBlock>>(
            blocks.Values
                .OrderByDescending(x => x.CreatedAt)
                .ToArray());
    }

    internal static UserBlock CreateBlock(
        string earthIdSub,
        string reason,
        string adminSubject,
        TimeSpan? duration)
    {
        if (string.IsNullOrWhiteSpace(earthIdSub))
            throw new ArgumentException(
                "EarthID subject is required.",
                nameof(earthIdSub));

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "Block reason is required.",
                nameof(reason));

        if (string.IsNullOrWhiteSpace(adminSubject))
            throw new ArgumentException(
                "Admin subject is required.",
                nameof(adminSubject));

        if (duration is <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Block duration must be positive when supplied.");

        var now = DateTimeOffset.UtcNow;
        return new UserBlock(
            earthIdSub.Trim(),
            reason.Trim(),
            adminSubject.Trim(),
            now,
            duration is null ? null : now.Add(duration.Value));
    }
}

public sealed class AdminAuditStore
{
    private const int Capacity = 1_000;
    private readonly ConcurrentQueue<AdminEnforcementEvent> events = new();

    public void Add(AdminEnforcementEvent item)
    {
        events.Enqueue(item);
        while (events.Count > Capacity)
            events.TryDequeue(out _);
    }

    public IReadOnlyList<AdminEnforcementEvent> GetRecent(int max) =>
        events
            .Reverse()
            .Take(Math.Clamp(max, 1, Capacity))
            .ToArray();
}
