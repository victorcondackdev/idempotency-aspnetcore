namespace Payments.Idempotency.AspNetCore;

/// <summary>The state of an idempotency key in the store.</summary>
public enum IdempotencyEntryState
{
    /// <summary>A request holding the lock is executing.</summary>
    InFlight = 0,

    /// <summary>The request finished; <see cref="IdempotencyEntry.Response"/> holds the stored response, if replayable.</summary>
    Completed = 1,
}

/// <summary>A stored idempotency key. Instances are immutable.</summary>
public sealed class IdempotencyEntry
{
    /// <summary>SHA-256 fingerprint (hex) of the request that created the entry.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The state of the key.</summary>
    public required IdempotencyEntryState State { get; init; }

    /// <summary>Opaque token identifying the request that holds (or held) the lock.</summary>
    public required string LockToken { get; init; }

    /// <summary>When the entry was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the entry expires: the lock timeout while in flight, the time to live once completed.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The stored response of a completed request, or <see langword="null"/> while in flight and
    /// for completed requests whose response could not be captured (too large or streamed).
    /// </summary>
    public StoredResponse? Response { get; init; }

    /// <summary>Whether the entry has expired at <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when <see cref="ExpiresAt"/> is not after <paramref name="now"/>.</returns>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>A captured HTTP response, replayed verbatim for retries.</summary>
public sealed class StoredResponse
{
    /// <summary>The HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>The selected response headers, including <c>Content-Type</c> when present.</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    /// <summary>The response body.</summary>
    public required byte[] Body { get; init; }
}
