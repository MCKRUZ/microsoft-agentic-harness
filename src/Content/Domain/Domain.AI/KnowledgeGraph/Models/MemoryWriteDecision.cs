namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The outcome of evaluating a candidate memory write through the memory write gate.
/// Immutable value object returned by <c>IMemoryWriteGate.EvaluateAsync</c> and applied by
/// <c>KnowledgeMemoryService.RememberAsync</c> before a fact is persisted.
/// </summary>
public sealed record MemoryWriteDecision
{
    /// <summary>
    /// Whether the fact should be persisted at all. <see langword="false"/> when the candidate
    /// content tripped the reject threshold (e.g. a critical prompt-injection match) — the fact is
    /// dropped and only the rejection is audited, so attacker payloads are never stored verbatim.
    /// </summary>
    public required bool Persist { get; init; }

    /// <summary>
    /// The trust classification to stamp on the persisted node. <see cref="MemoryTrust.Untrusted"/>
    /// facts are written but quarantined from recall. Ignored when <see cref="Persist"/> is false.
    /// </summary>
    public required MemoryTrust Trust { get; init; }

    /// <summary>
    /// Provenance metadata to attach to the persisted node, or <see langword="null"/> when
    /// provenance stamping is disabled. Records that the fact originated from the conversation
    /// memory pipeline.
    /// </summary>
    public ProvenanceStamp? Provenance { get; init; }

    /// <summary>
    /// A short, log- and audit-safe explanation of the decision
    /// (e.g. <c>"trusted"</c>, <c>"quarantined: DirectOverride"</c>, <c>"rejected: Critical"</c>).
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// A decision to persist the fact as trusted with no provenance stamp. Used as the
    /// zero-overhead result when the memory guard is disabled.
    /// </summary>
    public static MemoryWriteDecision Allow() =>
        new() { Persist = true, Trust = MemoryTrust.Trusted, Reason = "guard disabled" };
}
