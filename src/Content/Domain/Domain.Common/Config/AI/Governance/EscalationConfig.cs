namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Root configuration for the human escalation subsystem.
/// Bound from <c>AppConfig:AI:Governance:Escalation</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.Governance.Escalation
/// ├── Enabled                  — Master toggle for escalation
/// ├── DefaultTimeoutSeconds    — Global escalation timeout
/// ├── DefaultTimeoutAction     — Deny / DenyAndEscalate / Approve / Escalate
/// ├── DefaultApprovalStrategy  — AnyOf / AllOf / Quorum
/// ├── AuditStoragePath          — Directory for JSONL audit log
/// ├── RetryAttribution          — Retry-attribution card sizing (#325)
/// │   └── MaxPriorFailureLength — Soft producer-side cap on the prior-failure text
/// └── PriorityLevels{}         — Per-priority overrides keyed by EscalationPriority name
///     ├── TimeoutSeconds       — Override timeout for this level
///     ├── Async                — Non-blocking mode (informational)
///     └── EscalateToAll        — Notify all approvers simultaneously (critical)
/// </code>
/// </para>
/// </remarks>
public class EscalationConfig
{
    /// <summary>
    /// Whether the escalation system is active. When disabled, governance treats an
    /// approval-required decision as a denial (fail-closed).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long (in seconds) to wait for approver responses before firing the timeout action.
    /// Zero is valid for informational-only escalations.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Action taken when escalation times out. String value of <c>EscalationTimeoutAction</c>
    /// enum: "Deny", "DenyAndEscalate", "Approve", "Escalate".
    /// Validated at the Application layer.
    /// </summary>
    public string DefaultTimeoutAction { get; set; } = "DenyAndEscalate";

    /// <summary>
    /// Default approval strategy when a governance rule does not specify one.
    /// String value of <c>ApprovalStrategyType</c> enum: "AnyOf", "AllOf", "Quorum".
    /// Validated at the Application layer.
    /// </summary>
    public string DefaultApprovalStrategy { get; set; } = "AnyOf";

    /// <summary>
    /// Per-priority-level overrides keyed by <c>EscalationPriority</c> name
    /// ("Informational", "Blocking", "Critical").
    /// </summary>
    public Dictionary<string, EscalationPriorityConfig> PriorityLevels { get; set; } = new();

    /// <summary>
    /// Directory path for the JSONL escalation audit store.
    /// Relative paths are resolved from the application working directory.
    /// </summary>
    public string AuditStoragePath { get; set; } = ".agent-sessions/escalations";

    /// <summary>
    /// The claim type the escalation HTTP surface reads from the authenticated principal to
    /// establish the caller's approver identity — the name compared (case-insensitively, via
    /// <c>ApproverNames.Comparer</c>) against escalation rosters. Only identity-bearing claim
    /// types are accepted: <c>oid</c>, <c>sub</c>, <c>preferred_username</c>, or <c>upn</c>
    /// (enforced at startup by the config validator while escalation is enabled; resolution also
    /// searches each type's JWT inbound-mapped form on the principal). Defaults to <c>preferred_username</c>, the
    /// Entra ID v2.0 sign-in name, because the harness's roster surfaces (human-gate step
    /// config, governance rules) are authored with human-readable names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately a server-side setting, never a request parameter: the approver name
    /// must come from the validated token, so a caller can never assert an identity the token
    /// does not carry. A request whose principal lacks this claim — or carries it more than
    /// once — is rejected (fail-closed) rather than falling back to another claim.
    /// </para>
    /// <para>
    /// <b>Production recommendation: <c>oid</c>.</b> <c>preferred_username</c> and <c>upn</c>
    /// are mutable and reassignable — when a departed approver's UPN is reissued to a new hire,
    /// the new account silently inherits every roster entry naming that UPN. The immutable
    /// object id (<c>oid</c>, or <c>sub</c> for non-Entra issuers) has no such reuse window;
    /// author rosters with object ids and set this to <c>oid</c> for production. Hosts that keep
    /// a mutable claim type get a startup warning naming this risk.
    /// </para>
    /// </remarks>
    public string ApproverClaimType { get; set; } = "preferred_username";

    /// <summary>
    /// Sizing for the retry-attribution card (#325): when a corrected retry follows a failed
    /// approved attempt, how much of the prior failure's text is shown to the next approver.
    /// </summary>
    public EscalationRetryAttributionConfig RetryAttribution { get; set; } = new();

    /// <summary>
    /// Bounds on the reviewer-revision cycle (#321): how many times a single action may be sent
    /// back for revision before the escalation service resolves it as denied instead of opening
    /// another round.
    /// </summary>
    public EscalationRevisionConfig Revision { get; set; } = new();
}

/// <summary>Sizing for the retry-attribution text shown on a second-or-later approval attempt.</summary>
public sealed class EscalationRetryAttributionConfig
{
    /// <summary>
    /// Soft producer-side cap, in characters, on <c>EscalationRequest.PriorFailureReason</c>. Tied
    /// by <c>EscalationConfigValidator</c> to <c>EscalationRequestInvariants.MaxPriorFailureReasonLength</c>
    /// (the hard runtime ceiling) so the two can never be configured into disagreement.
    /// </summary>
    public int MaxPriorFailureLength { get; set; } = 512;
}

/// <summary>Bounds on the reviewer-revision cycle (#321).</summary>
public sealed class EscalationRevisionConfig
{
    /// <summary>
    /// Maximum number of revision rounds a single action may go through. The escalation service
    /// resolves a <c>Revise</c> verdict at this round as denied instead of opening another round.
    /// Tied by <c>EscalationConfigValidator</c> to the runtime ceiling on
    /// <c>EscalationRequest.RevisionRound</c> so the two can never disagree.
    /// </summary>
    public int MaxRounds { get; set; } = 2;
}
