namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the <c>ChangeProposal</c> pipeline.
/// Bound from <c>AppConfig:AI:Changes</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is off by default (<see cref="Enabled"/> is false). Even once
/// enabled, the orchestrator defaults to <c>Shadow</c> mode — gates evaluate and
/// audit, but the <c>MergeGate</c> short-circuits before invoking any
/// <c>IChangeApplier</c>. Operators flip individual skills to <c>Live</c> per
/// target type after they've tuned gates against real shadow traffic.
/// </para>
/// </remarks>
public sealed class ChangesConfig
{
    /// <summary>
    /// Master toggle. When false, the orchestrator and all CQRS commands fail
    /// fast and nothing in the pipeline runs — zero behavioural change in the
    /// host process.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Default orchestrator mode applied when a proposal does not carry an
    /// explicit one. Conservative default is <c>Shadow</c> so a misconfigured
    /// rollout never silently applies real changes.
    /// </summary>
    public string DefaultMode { get; set; } = "Shadow";

    /// <summary>
    /// Directory path for the JSONL gate-decision audit. Relative paths resolve
    /// from the application working directory. Same shape as the existing
    /// escalation and drift audit paths.
    /// </summary>
    public string AuditStoragePath { get; set; } = ".agent-sessions/changes";

    /// <summary>
    /// Directory path for content-addressed gate evidence. Lives next to the
    /// audit so an audit-line's <c>evidence_hash</c> resolves to a file in this
    /// directory. Relative paths resolve from the application working directory.
    /// </summary>
    public string EvidenceStoragePath { get; set; } = ".agent-sessions/changes/evidence";

    /// <summary>
    /// Hard upper bound on consecutive <c>GateAction.Defer</c> responses from
    /// the same gate against the same proposal. After this many defers the
    /// orchestrator promotes the proposal to <c>Rejected</c> with a
    /// "defer budget exhausted" reason. Prevents an unhealthy gate from
    /// keeping a proposal pinned indefinitely.
    /// </summary>
    public int MaxConsecutiveDefers { get; set; } = 20;

    /// <summary>
    /// Minimum <c>PolicyFindingSeverity</c> (as a string: <c>Info</c>,
    /// <c>Low</c>, <c>Medium</c>, <c>High</c>, <c>Critical</c>) that causes
    /// the <c>PolicyGate</c> to <c>Fail</c> the proposal. Findings below this
    /// threshold are still captured in the audit but do not block. Default
    /// <c>High</c> matches the convention used by the existing
    /// <c>GovernanceConfig.InjectionBlockThreshold</c>.
    /// </summary>
    public string PolicyBlockingSeverity { get; set; } = "High";

    /// <summary>
    /// Default approver identifiers used by the
    /// <c>EscalationServiceApprovalRouter</c> when surfacing a proposal for
    /// human approval. Empty list means the consumer has not configured
    /// approvers — the router fails fast rather than silently producing an
    /// escalation that nobody can act on. Override per-skill via a custom
    /// <c>IChangeApprovalRouter</c> implementation.
    /// </summary>
    public List<string> DefaultApprovers { get; set; } = [];

    /// <summary>
    /// When false (default), the registered startup validator refuses to boot
    /// if the pipeline is <see cref="Enabled"/>, the active
    /// <c>IChangeProposalStore</c> is the in-memory implementation, and
    /// <c>IHostEnvironment</c> is not Development. Set true only when the
    /// consumer has accepted that proposal state is lost across host restarts
    /// (e.g. single-process trials, integration test rigs).
    /// </summary>
    /// <remarks>
    /// In-memory state loss in production silently strands proposals at
    /// <c>AwaitingApproval</c> across deploys, which the escalation system
    /// can't surface. Fail-fast at boot is louder than fail-quiet at restart.
    /// </remarks>
    public bool AllowInMemoryStoreOutsideDevelopment { get; set; }
}
