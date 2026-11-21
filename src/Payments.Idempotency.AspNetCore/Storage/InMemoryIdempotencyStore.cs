using System.Collections.Concurrent;

namespace Payments.Idempotency.AspNetCore;

/// <summary>
/// Thread-safe, process-local <see cref="IIdempotencyStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Suitable for a single instance, development
/// and tests; use a shared store (Redis, SQL) when the API runs on more than one node.
/// </summary>
/// <remarks>
/// Every state change is a compare-and-swap on the dictionary entry (entries are immutable and
/// compared by reference), so concurrent requests with the same key cannot both acquire the lock.
/// Expired entries are removed lazily and by a periodic sweep piggybacking on writes.
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, IdempotencyEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private long _nextSweepTicks;

    /// <summary>Creates the store.</summary>
    /// <param name="timeProvider">The clock used for expiry.</param>
    public InMemoryIdempotencyStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _nextSweepTicks = timeProvider.GetUtcNow().Add(SweepInterval).UtcTicks;
    }

    /// <summary>The number of entries currently held, including expired ones not yet swept.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public Task<IdempotencyBeginResult> TryBeginAsync(string key, string fingerprint, TimeSpan lockTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(fingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        SweepIfDue(now);

        var candidate = new IdempotencyEntry
        {
            Fingerprint = fingerprint,
            State = IdempotencyEntryState.InFlight,
            LockToken = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            ExpiresAt = now + lockTimeout,
        };

        while (true)
        {
            if (_entries.TryAdd(key, candidate))
            {
                return Task.FromResult(IdempotencyBeginResult.Locked(candidate.LockToken));
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                if (!existing.IsExpired(now))
                {
                    return Task.FromResult(IdempotencyBeginResult.Exists(existing));
                }

                // Expired: take it over only if nobody else did in the meantime.
                if (_entries.TryUpdate(key, candidate, existing))
                {
                    return Task.FromResult(IdempotencyBeginResult.Locked(candidate.LockToken));
                }
            }

            // Lost a race (removed or replaced concurrently): try again.
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string key, string lockToken, StoredResponse? response, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);

        if (!_entries.TryGetValue(key, out var current) || !IsHeldBy(current, lockToken))
        {
            return Task.FromResult(false);
        }

        var now = _timeProvider.GetUtcNow();
        var completed = new IdempotencyEntry
        {
            Fingerprint = current.Fingerprint,
            State = IdempotencyEntryState.Completed,
            LockToken = lockToken,
            CreatedAt = current.CreatedAt,
            ExpiresAt = now + timeToLive,
            Response = response,
        };

        return Task.FromResult(_entries.TryUpdate(key, completed, current));
    }

    /// <inheritdoc />
    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!_entries.TryGetValue(key, out var entry))
        {
            return Task.FromResult<IdempotencyEntry?>(null);
        }

        if (entry.IsExpired(_timeProvider.GetUtcNow()))
        {
            _entries.TryRemove(new KeyValuePair<string, IdempotencyEntry>(key, entry));
            return Task.FromResult<IdempotencyEntry?>(null);
        }

        return Task.FromResult<IdempotencyEntry?>(entry);
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(string key, string lockToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(lockToken);

        if (!_entries.TryGetValue(key, out var current) || !IsHeldBy(current, lockToken))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_entries.TryRemove(new KeyValuePair<string, IdempotencyEntry>(key, current)));
    }

    private static bool IsHeldBy(IdempotencyEntry entry, string lockToken) =>
        entry.State == IdempotencyEntryState.InFlight && string.Equals(entry.LockToken, lockToken, StringComparison.Ordinal);

    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due ||
            Interlocked.CompareExchange(ref _nextSweepTicks, now.Add(SweepInterval).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (pair.Value.IsExpired(now))
            {
                _entries.TryRemove(pair);
            }
        }
    }
}
