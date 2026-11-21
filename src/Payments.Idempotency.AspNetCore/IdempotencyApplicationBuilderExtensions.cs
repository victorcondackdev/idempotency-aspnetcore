using Payments.Idempotency.AspNetCore;
using Payments.Idempotency.AspNetCore.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds the idempotency middleware to the request pipeline.</summary>
public static class IdempotencyApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the idempotency middleware. It acts only on endpoints marked with
    /// <c>RequireIdempotency()</c> or <see cref="IdempotentAttribute"/>, so it must run after
    /// routing (which <c>WebApplication</c> adds automatically) and before the endpoints. Place it
    /// after authentication when <see cref="IdempotencyOptions.ScopeResolver"/> uses the user.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><c>services.AddIdempotency()</c> was not called.</exception>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.ApplicationServices.GetService(typeof(IIdempotencyStore)) is null)
        {
            throw new InvalidOperationException(
                "Idempotency services are not registered. Call services.AddIdempotency() before app.UseIdempotency().");
        }

        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
