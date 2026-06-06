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
}
