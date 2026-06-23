namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Optional "Task Adherence" check for the memory write gate: judges whether a candidate fact reads
/// as a legitimate, in-scope thing to remember versus an embedded directive or out-of-scope
/// instruction that a poisoned conversation is trying to plant. The harness analogue of Microsoft's
/// memory-tool Task Adherence check.
/// </summary>
/// <remarks>
/// <para>
/// Ships with a fail-open no-op default (<c>NoOpMemoryIntentClassifier</c>) that reports every
/// candidate as aligned, so the gate's always-on protection stays the deterministic injection scan.
/// Consumers replace it with a real (typically LLM-backed) implementation and set
/// <c>MemoryGuardConfig.IntentCheckEnabled</c> to <see langword="true"/> to activate it.
/// </para>
/// <para>
/// The check is intentionally content-intrinsic — it sees the candidate fact, not the originating
/// user turn — so it can run at the write chokepoint without threading conversation context through
/// the memory interface. A future enhancement can widen the seam to compare against the user's
/// original request.
/// </para>
/// </remarks>
public interface IMemoryIntentClassifier
{
    /// <summary>
    /// Judges whether the candidate fact is an in-scope thing to remember.
    /// </summary>
    /// <param name="content">The candidate fact content.</param>
    /// <param name="entityType">The entity type for the candidate fact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The alignment result; <c>Aligned == false</c> causes the gate to quarantine the fact.</returns>
    Task<MemoryIntentResult> ClassifyAsync(
        string content,
        string entityType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of an <see cref="IMemoryIntentClassifier"/> check.
/// </summary>
/// <param name="Aligned">Whether the candidate fact is an in-scope, legitimate thing to remember.</param>
/// <param name="Reason">An optional short, audit-safe explanation when not aligned.</param>
public readonly record struct MemoryIntentResult(bool Aligned, string? Reason = null)
{
    /// <summary>A result indicating the candidate is aligned and may be remembered.</summary>
    public static MemoryIntentResult AlignedResult { get; } = new(true);
}
