using Payments.Idempotency.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Endpoint conventions that enable idempotency on minimal API endpoints and groups.</summary>
public static class IdempotencyEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Enables idempotency for the endpoint (or every endpoint of a group). Requires
    /// <c>app.UseIdempotency()</c> in the pipeline.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="required">
    /// Whether requests without the idempotency header are rejected with <c>400</c>. When
    /// <see langword="false"/>, requests without the header are executed normally.
    /// </param>
    /// <param name="timeToLive">The replay window, or <see langword="null"/> for <see cref="IdempotencyOptions.TimeToLive"/>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder, bool required = true, TimeSpan? timeToLive = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive), timeToLive, "The time to live must be positive.");
        }

        var metadata = new IdempotencyEndpointMetadata(required, timeToLive);
        builder.Add(endpoint => endpoint.Metadata.Add(metadata));
        return builder;
    }
}
