using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public interface IAccessPolicyCache
{
    ValueTask<AccessPolicyDecision?> GetAsync(
        AccessPolicyRequest request,
        CancellationToken cancellationToken);

    ValueTask SetAsync(
        AccessPolicyRequest request,
        AccessPolicyDecision decision,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    ValueTask<long> InvalidateApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken);
}

public sealed class InMemoryAccessPolicyCache : IAccessPolicyCache
{
    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public ValueTask<AccessPolicyDecision?> GetAsync(
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = AccessPolicyCacheKey.Build(request);
        if (!entries.TryGetValue(key, out var entry))
            return ValueTask.FromResult<AccessPolicyDecision?>(null);

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            entries.TryRemove(key, out _);
            return ValueTask.FromResult<AccessPolicyDecision?>(null);
        }

        return ValueTask.FromResult<AccessPolicyDecision?>(entry.Decision);
    }

    public ValueTask SetAsync(
        AccessPolicyRequest request,
        AccessPolicyDecision decision,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero)
            return ValueTask.CompletedTask;

        entries[AccessPolicyCacheKey.Build(request)] =
            new Entry(request.Application, decision, DateTimeOffset.UtcNow.Add(ttl));
        return ValueTask.CompletedTask;
    }

    public ValueTask<long> InvalidateApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long removed = 0;
        foreach (var pair in entries)
        {
            if (!string.Equals(pair.Value.Application, applicationId, StringComparison.Ordinal))
                continue;

            if (entries.TryRemove(pair.Key, out _))
                removed++;
        }

        return ValueTask.FromResult(removed);
    }

    private sealed record Entry(
        string Application,
        AccessPolicyDecision Decision,
        DateTimeOffset ExpiresAt);
}

public sealed class RedisAccessPolicyCache(
    IConnectionMultiplexer redis,
    ILogger<RedisAccessPolicyCache> logger) : IAccessPolicyCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<AccessPolicyDecision?> GetAsync(
        AccessPolicyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = await redis.GetDatabase()
                .StringGetAsync(CacheKey(request))
                .WaitAsync(cancellationToken);
            if (value.IsNullOrEmpty)
                return null;

            return JsonSerializer.Deserialize<AccessPolicyDecision>(
                value.ToString(),
                JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RedisException or JsonException)
        {
            logger.LogWarning(
                ex,
                "access_policy_cache_read_failed application={Application}",
                request.Application);
            return null;
        }
    }

    public async ValueTask SetAsync(
        AccessPolicyRequest request,
        AccessPolicyDecision decision,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero)
            return;

        try
        {
            var db = redis.GetDatabase();
            var key = CacheKey(request);
            var index = IndexKey(request.Application);
            var payload = JsonSerializer.Serialize(decision, JsonOptions);
            var batch = db.CreateBatch();
            var set = batch.StringSetAsync(key, payload, ttl);
            var indexAdd = batch.SetAddAsync(index, key.ToString());
            var indexExpire = batch.KeyExpireAsync(index, TimeSpan.FromMinutes(10));
            batch.Execute();
            await Task.WhenAll(set, indexAdd, indexExpire)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisException ex)
        {
            // Cache is an optimization. Authorization still remains fail-closed
            // because the underlying Access API decision was already obtained.
            logger.LogWarning(
                ex,
                "access_policy_cache_write_failed application={Application}",
                request.Application);
        }
    }

    public async ValueTask<long> InvalidateApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var db = redis.GetDatabase();
            var index = IndexKey(applicationId);
            var members = await db.SetMembersAsync(index)
                .WaitAsync(cancellationToken);
            if (members.Length == 0)
            {
                await db.KeyDeleteAsync(index).WaitAsync(cancellationToken);
                return 0;
            }

            var keys = members
                .Where(x => !x.IsNullOrEmpty)
                .Select(x => (RedisKey)x.ToString())
                .Append(index)
                .ToArray();
            return await db.KeyDeleteAsync(keys)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisException ex)
        {
            logger.LogError(
                ex,
                "access_policy_cache_invalidation_failed application={Application}",
                applicationId);
            throw new InvalidOperationException(
                "Access policy cache backend is unavailable.",
                ex);
        }
    }

    private static RedisKey CacheKey(AccessPolicyRequest request) =>
        $"eiag:policy:{AccessPolicyCacheKey.Build(request)}";

    private static RedisKey IndexKey(string applicationId)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(applicationId)));
        return $"eiag:policy-index:{hash}";
    }
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
        var cached = await cache.GetAsync(request, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug(
                "access_policy_cache_hit application={Application} service={Service} layer={LayerId} operation={Operation}",
                request.Application,
                request.Service,
                request.LayerId,
                request.Operation);
            return cached;
        }

        var decision = await inner.AuthorizeAsync(
            application,
            request,
            cancellationToken);

        if (decision.CacheTtlSeconds is > 0)
        {
            await cache.SetAsync(
                request,
                decision,
                TimeSpan.FromSeconds(decision.CacheTtlSeconds.Value),
                cancellationToken);
        }

        return decision;
    }
}

internal static class AccessPolicyCacheKey
{
    public static string Build(AccessPolicyRequest request)
    {
        var raw = string.Join(
            '\n',
            request.EarthIdSub,
            request.Tenant ?? "-",
            request.Application,
            request.Service,
            request.ServiceType,
            request.LayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            request.Operation,
            request.Method.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
