namespace Payments.Idempotency.AspNetCore;

/// <summary>
/// Enables idempotency on an MVC controller or action. Requires <c>app.UseIdempotency()</c> in
/// the pipeline.
/// </summary>
/// <example>
/// <code>
/// [HttpPost, Idempotent(TimeToLiveSeconds = 3600)]
/// public IActionResult Refund(RefundRequest request) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute, IIdempotencyMetadata
{
    /// <summary>Whether requests without the idempotency header are rejected with <c>400</c>. Defaults to <see langword="true"/>.</summary>
    public bool Required { get; set; } = true;

    /// <summary>The replay window in seconds; zero or less uses <see cref="IdempotencyOptions.TimeToLive"/>.</summary>
    public int TimeToLiveSeconds { get; set; }

    /// <inheritdoc />
    TimeSpan? IIdempotencyMetadata.TimeToLive => TimeToLiveSeconds > 0 ? TimeSpan.FromSeconds(TimeToLiveSeconds) : null;
}
