using Microsoft.Extensions.DependencyInjection.Extensions;
using Payments.Idempotency.AspNetCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the idempotency services.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IdempotencyOptions"/>, <see cref="TimeProvider.System"/> (unless a
    /// <see cref="TimeProvider"/> is already registered) and, unless another store is registered,
    /// the process-local <see cref="InMemoryIdempotencyStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="IdempotencyOptions"/>.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddIdempotency(this IServiceCollection services, Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IdempotencyOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.HeaderName), "HeaderName must not be empty.")
            .Validate(o => o.MaxKeyLength > 0, "MaxKeyLength must be positive.")
            .Validate(o => o.TimeToLive > TimeSpan.Zero, "TimeToLive must be positive.")
            .Validate(o => o.LockTimeout > TimeSpan.Zero, "LockTimeout must be positive.")
            .Validate(o => o.MaxResponseBodySize >= 0, "MaxResponseBodySize must not be negative.");

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }

    /// <summary>
    /// Replaces the store with <see cref="DistributedCacheIdempotencyStore"/>. Register an
    /// <c>IDistributedCache</c> as well (for example <c>AddStackExchangeRedisCache</c>). Read the
    /// atomicity caveat on <see cref="DistributedCacheIdempotencyStore"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDistributedCacheIdempotencyStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore, DistributedCacheIdempotencyStore>());
        return services;
    }

    /// <summary>Replaces the store with a custom <see cref="IIdempotencyStore"/> implementation.</summary>
    /// <typeparam name="TStore">The store type, registered as a singleton.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddIdempotencyStore<TStore>(this IServiceCollection services)
        where TStore : class, IIdempotencyStore
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore, TStore>());
        return services;
    }
}
