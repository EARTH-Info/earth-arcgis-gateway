using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public sealed record AccessPolicyCacheLookup(
    AccessPolicyDecision? Decision,
    long Generation,
    bool Available = true);

public interface IAccessPolicyCache
{
    ValueTask<AccessPolicyCacheLookup> GetAsync(
        AccessPolicyRequest request,
        CancellationToken cancellationToken);

    ValueTask SetAsync(
        AccessPolicyRequest request,
        AccessPolicyDecision decision,
        TimeSpan ttl,
        long expectedGeneration,
        CancellationToken cancellationToken);

    ValueTask<long> InvalidateApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken);
}

public sealed class InMemoryAccessPolicyCache : IAccessPolicyCache
{
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly ConcurrentDictionary<string, long> generations = new(StringComparer.Ordinal);

    public ValueTask<AccessPolicyCacheLookup> GetAsync(
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = generations.GetOrAdd(request.Application, 0);
        var key = AccessPolicyCacheKey.Build(request, generation);
        if (!entries.TryGetValue(key, out var entry))
            return ValueTask.FromResult(new AccessPolicyCacheLookup(null, generation));

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            entries.TryRemove(key, out _);
            return ValueTask.FromResult(new AccessPolicyCacheLookup(null, generation));
        }

        return ValueTask.FromResult(new AccessPolicyCacheLookup(entry.Decision, generation));
    }

    public ValueTask SetAsync(
        AccessPolicyRequest request,
        AccessPolicyDecision decision,
        TimeSpan ttl,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero)
            return ValueTask.CompletedTask;

        var currentGeneration = generations.GetOrAdd(request.Application, 0);
        if (currentGeneration != expectedGeneration)
            return ValueTask.CompletedTask;

        entries[AccessPolicyCacheKey.Build(request, expectedGeneration)] =
            new Entry(decision, DateTimeOffset.UtcNow.Add(ttl));
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> InvalidateApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = generations.AddOrUpdate(
            applicationId,
            1,
            static (_, current) => checked(current + 1));
        return ValueTask.FromResult(generation);
    }

    private sealed record Entry(AccessPolicyDecision Decision, DateTimeOffset ExpiresAt);
}

public sealed class RedisAccessPolicyCache(
    IConnectionMultiplexer redis,
    ILogger<RedisAccessPolicyCache> logger) : IAccessPolicyCache
{
    private const int EntriesTtlSeconds = 10 * 60;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<AccessPolicyCacheLookup> GetAsync(
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var db = redis.GetDatabase();
            var generationKey = GenerationKey(request.Application);
            var generationBefore = await ReadGenerationAsync(db, generationKey, cancellationToken);
            var field = EntryField(request, generationBefore);
            var value = await db.HashGetAsync(EntriesKey(request.Application), field)
                .WaitAsync(cancellationToken);
            var generationAfter = await ReadGenerationAsync(db, generationKey, cancellationToken);

            if (generationBefore != generationAfter || value.IsNullOrEmpty)
                return new AccessPolicyCacheLookup(null, generationAfter);

            var envelope = JsonSerializer.Deserialize<CachedDecision>(value.ToString(), JsonOptions);
            if (envelope is null || envelope.ExpiresAt <= DateTimeOffset.UtcNow)
                return new AccessPolicyCacheLookup(null, generationAfter);

            return new AccessPolicyCacheLookup(envelope.Decision, generationAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or JsonException or OverflowException)
        {
            logger.LogWarning(ex, "access_policy_cache_read_failed application={Application}", request.Application);
            return new AccessPolicyCacheLookup(null, -1, false);
        }
    }

    public async ValueTask SetAsync(
        AccessPolicyRequest request,
        AccessPolicyDecision decision,
        TimeSpan ttl,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero || expectedGeneration < 0)
            return;

        try
        {
            var db = redis.GetDatabase();
            var generationKey = GenerationKey(request.Application);
            var entriesKey = EntriesKey(request.Application);
            var transaction = db.CreateTransaction();
            transaction.AddCondition(expectedGeneration == 0
                ? Condition.KeyNotExists(generationKey)
                : Condition.StringEqual(generationKey, expectedGeneration));

            var envelope = new CachedDecision(decision, DateTimeOffset.UtcNow.Add(ttl));
            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            _ = transaction.HashSetAsync(entriesKey, EntryField(request, expectedGeneration), payload);
            _ = transaction.KeyExpireAsync(entriesKey, TimeSpan.FromSeconds(EntriesTtlSeconds));
            await transaction.ExecuteAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "access_policy_cache_write_failed application={Application}", request.Application);
        }
    }

    public async ValueTask<long> InvalidateApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await redis.GetDatabase()
                .StringIncrementAsync(GenerationKey(applicationId))
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or OverflowException)
        {
            logger.LogError(ex, "access_policy_cache_invalidation_failed application={Application}", applicationId);
            throw new InvalidOperationException("Access policy cache backend is unavailable.", ex);
        }
    }

    private static async Task<long> ReadGenerationAsync(
        IDatabase db,
        RedisKey key,
        CancellationToken cancellationToken)
    {
        var value = await db.StringGetAsync(key).WaitAsync(cancellationToken);
        if (value.IsNullOrEmpty)
            return 0;
        if (!long.TryParse(value.ToString(), out var generation) || generation < 0)
            throw new InvalidOperationException("Access policy cache generation is invalid.");
        return generation;
    }

    private static RedisValue EntryField(AccessPolicyRequest request, long generation) =>
        $"{generation}:{AccessPolicyCacheKey.BuildRequestHash(request)}";

    private static RedisKey GenerationKey(string applicationId) =>
        $"eiag:policy:{{{ApplicationHash(applicationId)}}}:generation";

    private static RedisKey EntriesKey(string applicationId) =>
        $"eiag:policy:{{{ApplicationHash(applicationId)}}}:entries";

    private static string ApplicationHash(string applicationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId)));

    private sealed record CachedDecision(AccessPolicyDecision Decision, DateTimeOffset ExpiresAt);
}

public sealed class CachingAccessPolicyClient(
    AccessPolicyClient inner,
    IAccessPolicyCache cache,
    ILogger<CachingAccessPolicyClient> logger) : IAccessPolicyClient
{
    public async Task<AccessPolicyDecision> AuthorizeAsync(
        GatewayApplication application,
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        var lookup = await cache.GetAsync(request, cancellationToken);
        if (lookup.Decision is not null)
        {
            logger.LogDebug(
                "access_policy_cache_hit application={Application} service={Service} layer={LayerId} operation={Operation} generation={Generation}",
                request.Application,
                request.Service,
                request.LayerId,
                request.Operation,
                lookup.Generation);
            return lookup.Decision;
        }

        var decision = await inner.AuthorizeAsync(application, request, cancellationToken);
        if (lookup.Available && decision.CacheTtlSeconds is > 0)
        {
            await cache.SetAsync(
                request,
                decision,
                TimeSpan.FromSeconds(decision.CacheTtlSeconds.Value),
                lookup.Generation,
                cancellationToken);
        }

        return decision;
    }
}

internal static class AccessPolicyCacheKey
{
    public static string Build(AccessPolicyRequest request, long generation) =>
        $"{generation}:{BuildRequestHash(request)}";

    public static string BuildRequestHash(AccessPolicyRequest request)
    {
        var raw = string.Join(
            '\n',
            request.EarthIdSub,
            request.Tenant ?? "-",
            request.Application,
            request.Service.Normalize(NormalizationForm.FormC),
            request.ServiceType.ToLowerInvariant(),
            request.LayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            request.Operation.ToLowerInvariant(),
            request.Method.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
