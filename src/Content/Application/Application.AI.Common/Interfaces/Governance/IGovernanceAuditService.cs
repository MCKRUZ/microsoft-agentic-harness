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
