using Domain.AI.KnowledgeGraph.Models;

namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Evaluates a candidate memory write before it is persisted, implementing the
/// "establish intent and provenance before persistence" principle. Sits at the single write
/// chokepoint (<c>IKnowledgeMemory.RememberAsync</c>) so it covers every write — including the
/// unattended post-turn auto-extraction path that bypasses the request pipeline's content-safety
/// and prompt-injection behaviors.
/// </summary>
/// <remarks>
/// The gate scans candidate content for prompt injection, optionally runs an intent
/// ("Task Adherence") check, and returns a <see cref="MemoryWriteDecision"/> classifying the fact
/// as persist-and-trust, persist-but-quarantine, or reject. It never mutates state itself; the
/// caller applies the decision. Implementations should be safe to register as singletons.
/// </remarks>
public interface IMemoryWriteGate
{
    /// <summary>
    /// Evaluates a candidate fact and returns the persistence decision.
    /// </summary>
    /// <param name="key">The memory key for the candidate fact.</param>
    /// <param name="content">The fact content to classify.</param>
    /// <param name="entityType">The entity type for the candidate node (e.g. "Fact", "Preference").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision to persist (trusted or quarantined) or reject the write.</returns>
    Task<MemoryWriteDecision> EvaluateAsync(
        string key,
        string content,
        string entityType,
        CancellationToken cancellationToken = default);
}
