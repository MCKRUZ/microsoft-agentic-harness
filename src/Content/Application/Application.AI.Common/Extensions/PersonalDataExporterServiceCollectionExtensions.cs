using Application.AI.Common.Interfaces.Compliance;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Extensions;

/// <summary>Registers a store as one section of every personal-data export (#902).</summary>
public static class PersonalDataExporterServiceCollectionExtensions
{
    /// <summary>
    /// Makes whatever is registered as <typeparamref name="TService"/> part of every personal-data export, when it can
    /// return one person's data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it beside the store's own registration and its <c>AddPersonalDataEraser</c> call. It forwards to the service as
    /// resolved, never to a concrete type, so the export always reads the instance the host actually uses. A store that
    /// does not implement <see cref="IPersonalDataExporter"/> (a deployer's replacement, or a null object) registers
    /// nothing here; when it can hold personal data its erasure registration names it in the export's manifest instead.
    /// </para>
    /// <para>
    /// Scoped, so it can forward to a scoped store as well as a singleton one. <c>EveryExportableStoreReachesTheExportTests</c>
    /// proves in the real composition that every store implementing <see cref="IPersonalDataExporter"/> the host resolves
    /// is one the export receives.
    /// </para>
    /// </remarks>
    /// <typeparam name="TService">The service the store is registered as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddPersonalDataExporter<TService>(this IServiceCollection services)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped(sp => (sp.GetService<TService>() as IPersonalDataExporter)!);
        return services;
    }
}
