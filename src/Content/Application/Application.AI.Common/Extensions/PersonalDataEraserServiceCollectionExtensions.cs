using System.Reflection;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Services.Compliance;
using Domain.Common.Compliance;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Extensions;

/// <summary>Registers a store as one step of right-to-erasure (#900).</summary>
public static class PersonalDataEraserServiceCollectionExtensions
{
    /// <summary>
    /// Makes whatever is registered as <typeparamref name="TService"/> part of every erasure request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it beside the store's own registration. It forwards to the service as resolved, never to a concrete type, so
    /// erasure always reaches the instance the host actually uses. Three outcomes:
    /// </para>
    /// <list type="bullet">
    /// <item><description>The store implements <see cref="IPersonalDataEraser"/>: it is erased.</description></item>
    /// <item><description>
    /// It carries <c>[PersonalDataStore]</c> but cannot erase (a deployer's replacement): a <see cref="NonErasableStore"/>
    /// stands in, so the receipt is <c>Partial</c> and names it rather than claiming the person is gone.
    /// </description></item>
    /// <item><description>It keeps nothing (a null object, with no descriptor): nothing is registered for it.</description></item>
    /// </list>
    /// <para>
    /// Scoped, so it can forward to a scoped store as well as a singleton one. <c>EveryErasableStoreReachesTheCoordinatorTests</c>
    /// proves in the real composition that every store implementing <see cref="IPersonalDataEraser"/> the host resolves
    /// is one the coordinator receives.
    /// </para>
    /// </remarks>
    /// <typeparam name="TService">The service the store is registered as.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="stageIfNotErasable">
    /// The stage a <see cref="NonErasableStore"/> stand-in takes. Only matters for a conversation store, whose stand-in must
    /// occupy <see cref="ErasureStage.Conversations"/> so the stage after it is held back.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddPersonalDataEraser<TService>(
        this IServiceCollection services, ErasureStage stageIfNotErasable = ErasureStage.BeforeConversations)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped(sp => sp.GetService<TService>() switch
        {
            IPersonalDataEraser eraser => eraser,
            { } store when store.GetType().GetCustomAttribute<PersonalDataStoreAttribute>() is not null =>
                new NonErasableStore(store.GetType().Name, stageIfNotErasable),
            _ => (IPersonalDataEraser)null!,
        });
        return services;
    }
}
