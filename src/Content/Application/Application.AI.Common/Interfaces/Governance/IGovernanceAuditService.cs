namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Tamper-evident governance audit logging with hash-chain integrity.
/// Complements the existing <c>IAuditSink</c> with governance-specific
/// hash-chain verification and event pub-sub.
/// </summary>
public interface IGovernanceAuditService
{
    /// <summary>
    /// Logs a governance decision to the tamper-evident audit chain.
    /// </summary>
    /// <remarks>
    /// <strong>Must never throw (#447).</strong> Every caller treats the audit write as a side effect
    /// of a decision already made — the tool call is already being allowed, denied, or refused before
    /// this runs — and none of this interface's 15+ production call sites expect or handle an
    /// exception from it. Several of those call sites sit inside a <c>try</c> block whose own
    /// legitimate failure return (e.g. a governance refusal, <c>Result.Fail</c>) would otherwise be
    /// silently replaced by this call's own unrelated I/O exception, turning a clean refusal into a
    /// propagating crash. A disk failure or similar here must degrade the audit trail's completeness,
    /// never the tool-call decision it is recording — "the audit is the record, not the control." An
    /// implementation should catch broadly, surface the failure through its own logging/metrics (never
    /// silently), and return normally regardless. See <c>JsonlGovernanceAuditWriter.Log</c>'s own
    /// remarks for the reference implementation of this contract, including why a write failure must
    /// still be visible to an operator even though it never throws.
    /// </remarks>
    /// <param name="agentId">The agent whose action was evaluated.</param>
    /// <param name="action">The action that was evaluated (e.g., tool name).</param>
    /// <param name="decision">The governance decision (allow, deny, warn, etc.).</param>
    void Log(string agentId, string action, string decision);

    /// <summary>
    /// Verifies the integrity of the entire audit chain.
    /// Returns false if any entry has been tampered with.
    /// </summary>
    bool VerifyChainIntegrity();

    /// <summary>Gets the total number of audit entries in the chain.</summary>
    int EntryCount { get; }
}
