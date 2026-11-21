namespace Payments.Idempotency.AspNetCore;

/// <summary>
/// Storage for idempotency keys. Implementations must make <see cref="TryBeginAsync"/> atomic:
/// for a given key, exactly one concurrent caller may acquire the lock.
/// </summary>
/// <remarks>
/// Keys passed to the store are already scoped and hashed by the middleware
/// (<c>{prefix}:{sha256(scope, method, route, key)}</c>), so they are safe to use verbatim in Redis
/// or SQL. Expiry is decided with the <see cref="TimeProvider"/> registered in DI and stored in
/// <see cref="IdempotencyEntry.ExpiresAt"/>; backends may additionally evict expired entries.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically creates an in-flight entry for <paramref name="key"/> unless a non-expired entry
    /// exists. Expired entries are replaced.
    /// </summary>
    /// <param name="key">The scoped store key.</param>
    /// <param name="fingerprint">The request fingerprint.</param>
    /// <param name="lockTimeout">How long the in-flight lock lives if never completed or released.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Either the acquired lock or the existing entry.</returns>
    Task<IdempotencyBeginResult> TryBeginAsync(string key, string fingerprint, TimeSpan lockTimeout, CancellationToken cancellationToken = default);

    /// <summary>Marks the in-flight entry as completed, storing the response for replay.</summary>
    /// <param name="key">The scoped store key.</param>
    /// <param name="lockToken">The token returned by <see cref="TryBeginAsync"/>.</param>
    /// <param name="response">The response to replay, or <see langword="null"/> when it could not be captured.</param>
    /// <param name="timeToLive">How long the completed entry is kept.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the caller no longer holds the lock (it expired and was taken over).</returns>
    Task<bool> CompleteAsync(string key, string lockToken, StoredResponse? response, TimeSpan timeToLive, CancellationToken cancellationToken = default);

    /// <summary>Gets the non-expired entry for <paramref name="key"/>, if any.</summary>
    /// <param name="key">The scoped store key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    Task<IdempotencyEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the in-flight entry so the key can be used again (for example after a 5xx).</summary>
    /// <param name="key">The scoped store key.</param>
    /// <param name="lockToken">The token returned by <see cref="TryBeginAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> when the caller no longer holds the lock.</returns>
    Task<bool> ReleaseAsync(string key, string lockToken, CancellationToken cancellationToken = default);
}
