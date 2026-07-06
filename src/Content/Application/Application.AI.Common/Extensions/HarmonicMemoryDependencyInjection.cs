using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services.KnowledgeGraph;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Registers the harmonic memory representation's Application-layer seams (Memora port).
/// </summary>
/// <remarks>
/// <para>
/// Ships the two producer seams with fail-fast <c>NotConfigured</c> defaults:
/// <list type="bullet">
///   <item><see cref="IMemoryAbstractor"/> → <see cref="NotConfiguredMemoryAbstractor"/>.</item>
///   <item><see cref="IMemoryConsolidator"/> → <see cref="NotConfiguredMemoryConsolidator"/>.</item>
/// </list>
/// </para>
/// <para>
/// Both defaults use <c>TryAddSingleton</c>, so an agent-backed replacement registered before this call
/// (typically in Infrastructure.AI) is preserved. The defaults are inert while
/// <c>AppConfig:AI:HarmonicMemory:Mode</c> is <c>Off</c> (the default) — nothing resolves them until a
/// consumer opts in — and throw with explicit guidance if reached without a real implementation.
/// </para>
/// </remarks>
public static class HarmonicMemoryDependencyInjection
{
    /// <summary>Registers the harmonic memory Application seams' fail-fast defaults.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHarmonicMemoryDependencies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryAbstractor, NotConfiguredMemoryAbstractor>();
        services.TryAddSingleton<IMemoryConsolidator, NotConfiguredMemoryConsolidator>();

        return services;
    }
}
