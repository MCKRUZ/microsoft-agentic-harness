using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Services.KnowledgeGraph;

/// <summary>
/// Fail-fast placeholder <see cref="IMemoryConsolidator"/>. See
/// <see cref="NotConfiguredMemoryAbstractor"/> for the rationale — it is only reached in
/// <see cref="Domain.Common.Config.AI.HarmonicMemory.HarmonicMemoryMode.Full"/>, and template consumers
/// replace it with an agent-backed implementation.
/// </summary>
public sealed class NotConfiguredMemoryConsolidator : IMemoryConsolidator
{
    /// <inheritdoc />
    public Task<MemoryConsolidationDecision> ConsolidateAsync(
        MemoryAbstraction candidate,
        string candidateValue,
        IReadOnlyList<MemoryRecord> similarExisting,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No IMemoryConsolidator is configured. Harmonic memory is enabled in Full mode " +
            "(AppConfig:AI:HarmonicMemory:Mode = Full) but the default NotConfiguredMemoryConsolidator " +
            "throws. Register an agent-backed implementation (e.g. in Infrastructure.AI), or use " +
            "AbstractOnly mode, which does not consolidate.");
}
