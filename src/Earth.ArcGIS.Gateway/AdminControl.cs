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
    bool TryGetActive(string earthIdSub, out UserBlock block);
    UserBlock Block(string earthIdSub, string reason, string adminSubject, TimeSpan? duration);
    bool Unblock(string earthIdSub);
    IReadOnlyList<UserBlock> GetActive();
}

public sealed class UserBlockStore : IUserBlockStore
{
    private readonly ConcurrentDictionary<string, UserBlock> blocks =
        new(StringComparer.Ordinal);

    public bool TryGetActive(string earthIdSub, out UserBlock block)
    {
        if (blocks.TryGetValue(earthIdSub, out var current))
        {
            if (current.IsActive(DateTimeOffset.UtcNow))
            {
                block = current;
                return true;
            }

            blocks.TryRemove(earthIdSub, out _);
        }

        block = null!;
        return false;
    }

    public UserBlock Block(
        string earthIdSub,
        string reason,
        string adminSubject,
        TimeSpan? duration)
    {
        if (string.IsNullOrWhiteSpace(earthIdSub))
            throw new ArgumentException("EarthID subject is required.", nameof(earthIdSub));

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Block reason is required.", nameof(reason));

        var now = DateTimeOffset.UtcNow;
        var block = new UserBlock(
            earthIdSub.Trim(),
            reason.Trim(),
            adminSubject,
            now,
            duration is null ? null : now.Add(duration.Value));

        blocks[block.EarthIdSub] = block;
        return block;
    }

    public bool Unblock(string earthIdSub) =>
        blocks.TryRemove(earthIdSub, out _);

    public IReadOnlyList<UserBlock> GetActive()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in blocks)
        {
            if (!pair.Value.IsActive(now))
                blocks.TryRemove(pair.Key, out _);
        }

        return blocks.Values
            .OrderByDescending(x => x.CreatedAt)
            .ToArray();
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
