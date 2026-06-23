namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Trust classification stamped on a remembered fact at write time by the memory write gate.
/// Drives the retrieval-time decision: only <see cref="Trusted"/> facts are returned by recall.
/// </summary>
/// <remarks>
/// <para>
/// The classification implements the "establish intent and provenance before persistence" and
/// "treat retrieval as a risk decision" principles from Microsoft's <em>Guarding AI memory</em>
/// guidance. A fact derived from content that the prompt-injection scanner flagged at or above the
/// configured quarantine threshold is persisted as <see cref="Untrusted"/> — it remains in the
/// store (so it is auditable and forensically recoverable) but is never served back to the agent.
/// </para>
/// <para>
/// This is <em>quarantine, not delete</em>: the node exists but is invisible to <c>RecallAsync</c>.
/// Content that trips the reject threshold is never written at all (only the rejection is audited),
/// so it never reaches this enum.
/// </para>
/// </remarks>
public enum MemoryTrust
{
    /// <summary>
    /// The fact is recallable. The default for any node without an explicit trust marker
    /// (e.g. legacy nodes, or writes made while the memory guard is disabled).
    /// </summary>
    Trusted,

    /// <summary>
    /// The fact was flagged at or above the quarantine threshold and is withheld from recall.
    /// It is retained in the store for audit and incident response, never returned to the agent.
    /// </summary>
    Untrusted
}
