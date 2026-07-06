using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Decides, for a candidate memory, whether to merge it into an existing entry or persist it as a new
/// one — the consolidation half of the harmonic memory write path (Memora port). Consolidation keeps the
/// store clean by aggregating related and evolving information under a single primary abstraction rather
/// than fragmenting it across redundant records.
/// </summary>
/// <remarks>
/// Reached only in <see cref="Domain.Common.Config.AI.HarmonicMemory.HarmonicMemoryMode.Full"/>. The
/// harness ships <see cref="Application.AI.Common.Services.KnowledgeGraph.NotConfiguredMemoryConsolidator"/>
/// as a fail-fast default; template consumers replace it with an agent-backed implementation. Model output
/// is untrusted and must be validated (an unknown <see cref="MemoryConsolidationDecision.TargetId"/> must
/// be treated as <see cref="ConsolidationAction.Create"/> by the caller).
/// </remarks>
public interface IMemoryConsolidator
{
    /// <summary>
    /// Decides whether <paramref name="candidate"/> should be merged into one of
    /// <paramref name="similarExisting"/> or created as a new entry.
    /// </summary>
    /// <param name="candidate">The candidate's abstraction and cue anchors.</param>
    /// <param name="candidateValue">The candidate's full memory value.</param>
    /// <param name="similarExisting">
    /// The most-similar existing entries (by abstraction), up to <c>HarmonicMemoryConfig.ConsolidationTopK</c>.
    /// Storage-neutral <see cref="ExistingMemory"/> views, so the seam is not coupled to any store type.
    /// May be empty, in which case implementations return <see cref="MemoryConsolidationDecision.Create"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token with the consolidation timeout.</param>
    /// <returns>A merge-into-existing or create-new decision.</returns>
    Task<MemoryConsolidationDecision> ConsolidateAsync(
        MemoryAbstraction candidate,
        string candidateValue,
        IReadOnlyList<ExistingMemory> similarExisting,
        CancellationToken cancellationToken = default);
}
