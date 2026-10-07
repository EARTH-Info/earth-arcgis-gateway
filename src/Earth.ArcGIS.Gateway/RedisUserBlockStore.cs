using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Earth.ArcGIS.Gateway;

public sealed class RedisUserBlockStore(
    IConnectionMultiplexer redis) : IUserBlockStore
{
    private const string KeyPrefix = "eiag:block:";
    private const string IndexKey = "eiag:blocks:index";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<UserBlock?> GetActiveAsync(
        string earthIdSub,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();
        var field = HashSubject(earthIdSub);
        var value = await db.StringGetAsync(KeyPrefix + field)
            .WaitAsync(cancellationToken);

        if (value.IsNullOrEmpty)
            return null;

        var block = Deserialize(value!);
        if (block.IsActive(DateTimeOffset.UtcNow))
            return block;

        await RemoveAsync(db, field, cancellationToken);
        return null;
    }

    public async ValueTask<UserBlock> BlockAsync(
        string earthIdSub,
        string reason,
        string adminSubject,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var block = UserBlockStore.CreateBlock(
            earthIdSub,
            reason,
            adminSubject,
            duration);

        var json = JsonSerializer.Serialize(block, JsonOptions);
        var field = HashSubject(block.EarthIdSub);
        var db = redis.GetDatabase();
        var transaction = db.CreateTransaction();

        var setTask = duration is null
            ? transaction.StringSetAsync(KeyPrefix + field, json)
            : transaction.StringSetAsync(KeyPrefix + field, json, duration.Value);
        var indexTask = transaction.HashSetAsync(IndexKey, field, json);

        var committed = await transaction.ExecuteAsync()
            .WaitAsync(cancellationToken);
        if (!committed)
            throw new InvalidOperationException("Unable to atomically persist distributed user block.");

        var stored = await setTask.WaitAsync(cancellationToken);
        _ = await indexTask.WaitAsync(cancellationToken);
        if (!stored)
            throw new InvalidOperationException("Unable to persist distributed user block.");

        return block;
    }

    public async ValueTask<bool> UnblockAsync(
        string earthIdSub,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();
        var field = HashSubject(earthIdSub);
        var transaction = db.CreateTransaction();
        var keyDeleteTask = transaction.KeyDeleteAsync(KeyPrefix + field);
        var indexDeleteTask = transaction.HashDeleteAsync(IndexKey, field);

        var committed = await transaction.ExecuteAsync()
            .WaitAsync(cancellationToken);
        if (!committed)
            throw new InvalidOperationException("Unable to atomically remove distributed user block.");

        var keyDeleted = await keyDeleteTask.WaitAsync(cancellationToken);
        var indexDeleted = await indexDeleteTask.WaitAsync(cancellationToken);
        return keyDeleted || indexDeleted;
    }

    public async ValueTask<IReadOnlyList<UserBlock>> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();
        var entries = await db.HashGetAllAsync(IndexKey)
            .WaitAsync(cancellationToken);

        if (entries.Length == 0)
            return Array.Empty<UserBlock>();

        var now = DateTimeOffset.UtcNow;
        var active = new List<UserBlock>(entries.Length);
        var expiredFields = new List<RedisValue>();

        foreach (var entry in entries)
        {
            var block = Deserialize(entry.Value);
            if (block.IsActive(now))
                active.Add(block);
            else
                expiredFields.Add(entry.Name);
        }

        if (expiredFields.Count > 0)
        {
            await db.HashDeleteAsync(IndexKey, expiredFields.ToArray())
                .WaitAsync(cancellationToken);
        }

        return active
            .OrderByDescending(x => x.CreatedAt)
            .ToArray();
    }

    private static async Task RemoveAsync(
        IDatabase db,
        string field,
        CancellationToken cancellationToken)
    {
        var transaction = db.CreateTransaction();
        var keyDeleteTask = transaction.KeyDeleteAsync(KeyPrefix + field);
        var indexDeleteTask = transaction.HashDeleteAsync(IndexKey, field);
        var committed = await transaction.ExecuteAsync()
            .WaitAsync(cancellationToken);
        if (!committed)
            throw new InvalidOperationException("Unable to atomically remove expired user block.");
        _ = await keyDeleteTask.WaitAsync(cancellationToken);
        _ = await indexDeleteTask.WaitAsync(cancellationToken);
    }

    private static string HashSubject(string earthIdSub)
    {
        if (string.IsNullOrWhiteSpace(earthIdSub))
            throw new ArgumentException("EarthID subject is required.", nameof(earthIdSub));

        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(earthIdSub.Trim())));
    }

    private static UserBlock Deserialize(RedisValue value)
    {
        try
        {
            return JsonSerializer.Deserialize<UserBlock>(value.ToString(), JsonOptions)
                   ?? throw new InvalidOperationException("Distributed user block payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Distributed user block payload is invalid.", ex);
        }
    }
}
