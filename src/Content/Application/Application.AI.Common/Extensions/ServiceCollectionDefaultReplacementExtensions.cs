using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Replaces a fail-fast placeholder registration with a real implementation, without overriding a
/// registration the consumer made themselves.
/// </summary>
/// <remarks>
/// <para>
/// Several seams ship with a <c>NotConfigured*</c> default that throws on first use (a patch proposer,
/// a rollout runner) so that a host which never configured one fails loudly rather than silently. The
/// infrastructure projects that provide the real implementation are composed in an order the consumer
/// does not control, and a plain <c>AddX</c> would override a custom implementation the consumer
/// registered earlier, while a <c>TryAddX</c> would lose to the placeholder if it was registered first.
/// </para>
/// <para>
/// This takes the one position that is right in every order: if the only registrations of the service
/// are the placeholder, they are replaced; if anything else is registered, nothing changes. When this
/// runs first, the application layer's own <c>TryAdd</c> of the placeholder then finds a registration
/// and skips.
/// </para>
/// </remarks>
public static class ServiceCollectionDefaultReplacementExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TImplementation"/> as <typeparamref name="TService"/> unless a
    /// registration other than <typeparamref name="TPlaceholder"/> already exists.
    /// </summary>
    /// <typeparam name="TService">The service seam.</typeparam>
    /// <typeparam name="TPlaceholder">The fail-fast default to replace.</typeparam>
    /// <typeparam name="TImplementation">The real implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The lifetime to register the implementation with.</param>
    /// <returns><see langword="true"/> when the implementation was registered.</returns>
    public static bool ReplacePlaceholder<TService, TPlaceholder, TImplementation>(
        this IServiceCollection services,
        ServiceLifetime lifetime)
        where TService : class
        where TPlaceholder : class, TService
        where TImplementation : class, TService
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = services.Where(d => d.ServiceType == typeof(TService)).ToList();
        if (existing.Any(d => d.ImplementationType != typeof(TPlaceholder)))
            return false;

        foreach (var descriptor in existing)
            services.Remove(descriptor);

        services.Add(ServiceDescriptor.Describe(typeof(TService), typeof(TImplementation), lifetime));
        return true;
    }
}
