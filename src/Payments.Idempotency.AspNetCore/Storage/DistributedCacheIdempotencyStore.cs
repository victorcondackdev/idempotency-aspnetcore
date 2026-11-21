using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Payments.Idempotency.AspNetCore;

/// <summary>
/// <see cref="IIdempotencyStore"/> on top of any <see cref="IDistributedCache"/> (Redis, SQL Server,
/// NCache, ...), so idempotency works across instances with infrastructure you already have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not strictly atomic.</b> <see cref="IDistributedCache"/> has no compare-and-set, so
/// <see cref="TryBeginAsync"/> is a read, a write and a read-back. Two requests racing within a few
/// milliseconds can both acquire the lock. The read-back catches most collisions, but a true
/// guarantee needs a native primitive such as Redis <c>SET key value NX PX ttl</c>; see the
/// README for a <c>RedisIdempotencyStore</c> sketch.
/// </para>
/// <para>
/// Entries are serialized as JSON. Expiry is enforced twice: by the cache
/// (<see cref="DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow"/>) and by comparing
/// <see cref="IdempotencyEntry.ExpiresAt"/> with the registered <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed class DistributedCacheIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the store.</summary>
    /// <param name="cache">The distributed cache.</param>
    /// <param name="timeProvider">The clock used for expiry.</param>
    public DistributedCacheIdempotencyStore(IDistributedCache cache, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _cache = cache;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<IdempotencyBeginResult> TryBeginAsync(string key, string fingerprint, TimeSpan lockTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var now = _timeProvider.GetUtcNow();
        var existing = await ReadAsync(key, now, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return IdempotencyBeginResult.Exists(existing);
        }

        var entry = new IdempotencyEntry
        {
            Fingerprint = fingerprint,
            State = IdempotencyEntryState.InFlight,
            LockToken = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            ExpiresAt = now + lockTimeout,
        };
        await WriteAsync(key, entry, now, cancellationToken).ConfigureAwait(false);

        // Read back: if a concurrent writer won, defer to it.
        var winner = await ReadAsync(key, now, cancellationToken).ConfigureAwait(false);
        if (winner is not null && !string.Equals(winner.LockToken, entry.LockToken, StringComparison.Ordinal))
        {
            return IdempotencyBeginResult.Exists(winner);
        }

        return IdempotencyBeginResult.Locked(entry.LockToken);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string key, string lockToken, StoredResponse? response, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);

        var now = _timeProvider.GetUtcNow();
        var current = await ReadAsync(key, now, cancellationToken).ConfigureAwait(false);
        if (current is null || !IsHeldBy(current, lockToken))
        {
            return false;
        }

        var completed = new IdempotencyEntry
        {
            Fingerprint = current.Fingerprint,
            State = IdempotencyEntryState.Completed,
            LockToken = lockToken,
            CreatedAt = current.CreatedAt,
            ExpiresAt = now + timeToLive,
            Response = response,
        };
        await WriteAsync(key, completed, now, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return ReadAsync(key, _timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(string key, string lockToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);

        var current = await ReadAsync(key, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (current is null || !IsHeldBy(current, lockToken))
        {
            return false;
        }

        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool IsHeldBy(IdempotencyEntry entry, string lockToken) =>
        entry.State == IdempotencyEntryState.InFlight && string.Equals(entry.LockToken, lockToken, StringComparison.Ordinal);

    private async Task<IdempotencyEntry?> ReadAsync(string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        CacheRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<CacheRecord>(bytes, SerializerOptions);
        }
        catch (JsonException)
        {
            // Unreadable (corrupt or from an incompatible version): treat as absent.
            return null;
        }

        if (record is null || record.ExpiresAt <= now)
        {
            return null;
        }

        return record.ToEntry();
    }

    private Task WriteAsync(string key, IdempotencyEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lifetime = entry.ExpiresAt - now;
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromSeconds(1),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(CacheRecord.From(entry), SerializerOptions);
        return _cache.SetAsync(key, bytes, options, cancellationToken);
    }

    /// <summary>Serialization shape, decoupled from the public model so it can evolve independently.</summary>
    internal sealed class CacheRecord
    {
        public int Version { get; set; } = 1;

        public string Fingerprint { get; set; } = string.Empty;

        public IdempotencyEntryState State { get; set; }

        public string LockToken { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public int? StatusCode { get; set; }

        public Dictionary<string, string[]>? Headers { get; set; }

        public byte[]? Body { get; set; }

        public static CacheRecord From(IdempotencyEntry entry) => new()
        {
            Fingerprint = entry.Fingerprint,
            State = entry.State,
            LockToken = entry.LockToken,
            CreatedAt = entry.CreatedAt,
            ExpiresAt = entry.ExpiresAt,
            StatusCode = entry.Response?.StatusCode,
            Headers = entry.Response is null ? null : new Dictionary<string, string[]>(entry.Response.Headers, StringComparer.OrdinalIgnoreCase),
            Body = entry.Response?.Body,
        };

        public IdempotencyEntry ToEntry() => new()
        {
            Fingerprint = Fingerprint,
            State = State,
            LockToken = LockToken,
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt,
            Response = StatusCode is { } status
                ? new StoredResponse
                {
                    StatusCode = status,
                    Headers = Headers is null
                        ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string[]>(Headers, StringComparer.OrdinalIgnoreCase),
                    Body = Body ?? [],
                }
                : null,
        };
    }
}
