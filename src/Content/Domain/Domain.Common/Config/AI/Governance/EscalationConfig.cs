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
    /// (enforced at startup by the config validator). Defaults to <c>preferred_username</c>, the
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
}
