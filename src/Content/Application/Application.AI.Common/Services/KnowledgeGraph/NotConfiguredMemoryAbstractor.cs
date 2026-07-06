using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Services.KnowledgeGraph;

/// <summary>
/// Fail-fast placeholder <see cref="IMemoryAbstractor"/>. Throws with explicit guidance when invoked —
/// the harmonic memory representation ships its data model and seams, but the agent-backed abstractor is
/// wiring the template consumer owns.
/// </summary>
/// <remarks>
/// Registered by default (via <c>TryAddSingleton</c>) so the harness can DI-resolve
/// <see cref="IMemoryAbstractor"/> without forcing every consumer to write a real impl on day one. It is
/// only ever reached when <c>AppConfig:AI:HarmonicMemory:Mode</c> is raised above
/// <see cref="Domain.Common.Config.AI.HarmonicMemory.HarmonicMemoryMode.Off"/>; while off (the default),
/// nothing calls it. Replace with an agent-backed implementation (e.g. in Infrastructure.AI) before
/// enabling harmonic memory.
/// </remarks>
public sealed class NotConfiguredMemoryAbstractor : IMemoryAbstractor
{
    /// <inheritdoc />
    public Task<MemoryAbstraction> AbstractAsync(string content, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No IMemoryAbstractor is configured. Harmonic memory is enabled " +
            "(AppConfig:AI:HarmonicMemory:Mode is not Off) but the default NotConfiguredMemoryAbstractor " +
            "throws. Register an agent-backed implementation (e.g. in Infrastructure.AI), or set Mode to Off.");
}
