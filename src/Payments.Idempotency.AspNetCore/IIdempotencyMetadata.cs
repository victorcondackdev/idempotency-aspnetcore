namespace Payments.Idempotency.AspNetCore;

/// <summary>
/// Endpoint metadata that enables idempotency for an endpoint. Added by
/// <c>RequireIdempotency()</c> on minimal APIs or by <see cref="IdempotentAttribute"/> on controllers.
/// </summary>
public interface IIdempotencyMetadata
{
    /// <summary>Whether requests without the idempotency header are rejected with <c>400</c>.</summary>
    bool Required { get; }

    /// <summary>The replay window for this endpoint, or <see langword="null"/> for <see cref="IdempotencyOptions.TimeToLive"/>.</summary>
    TimeSpan? TimeToLive { get; }
}

internal sealed record IdempotencyEndpointMetadata(bool Required, TimeSpan? TimeToLive) : IIdempotencyMetadata;
