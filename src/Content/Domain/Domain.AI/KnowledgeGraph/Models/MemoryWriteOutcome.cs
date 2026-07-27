namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// The tri-state outcome of a memory write once the write gate's <see cref="MemoryWriteDecision"/>
/// has been applied by the write path. This is the honest, caller-facing summary of what actually
/// happened to a fact: stored and recallable, stored but never served, or dropped entirely.
/// </summary>
/// <remarks>
/// Exposed (as its string name) on the HTTP memory surface so external writers can distinguish
/// "my fact will be recalled" from "my fact was quarantined by the prompt-injection gate" —
/// a silent void return would let a rejected write masquerade as a success.
/// </remarks>
public enum MemoryWriteOutcome
{
    /// <summary>The fact was persisted as trusted and is recallable.</summary>
    Persisted = 0,

    /// <summary>
    /// The fact was persisted for audit/forensics but classified untrusted — it will never be
    /// served by recall.
    /// </summary>
    Quarantined,

    /// <summary>
    /// The fact tripped the reject threshold (e.g. a critical prompt-injection match) and was
    /// not stored anywhere.
    /// </summary>
    Rejected
}
