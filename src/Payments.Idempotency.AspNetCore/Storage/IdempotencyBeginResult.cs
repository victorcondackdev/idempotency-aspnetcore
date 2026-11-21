namespace Payments.Idempotency.AspNetCore;

/// <summary>
/// The outcome of <see cref="IIdempotencyStore.TryBeginAsync"/>: either the caller acquired the
/// lock (and holds <see cref="LockToken"/>), or the key already exists (<see cref="Existing"/>).
/// </summary>
public sealed class IdempotencyBeginResult
{
    private IdempotencyBeginResult(string? lockToken, IdempotencyEntry? existing)
    {
        LockToken = lockToken;
        Existing = existing;
    }

    /// <summary>Whether the caller acquired the lock and must execute the request.</summary>
    public bool Acquired => LockToken is not null;

    /// <summary>The lock token to pass to <see cref="IIdempotencyStore.CompleteAsync"/> or <see cref="IIdempotencyStore.ReleaseAsync"/>.</summary>
    public string? LockToken { get; }

    /// <summary>The existing entry when the lock was not acquired.</summary>
    public IdempotencyEntry? Existing { get; }

    /// <summary>Creates a result for an acquired lock.</summary>
    /// <param name="lockToken">The token identifying the lock holder.</param>
    /// <returns>The result.</returns>
    public static IdempotencyBeginResult Locked(string lockToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockToken);
        return new IdempotencyBeginResult(lockToken, null);
    }

    /// <summary>Creates a result for a key that already exists.</summary>
    /// <param name="existing">The existing entry.</param>
    /// <returns>The result.</returns>
    public static IdempotencyBeginResult Exists(IdempotencyEntry existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return new IdempotencyBeginResult(null, existing);
    }
}
