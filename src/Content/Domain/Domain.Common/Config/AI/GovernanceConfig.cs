using Domain.Common.Config.AI.Governance;

namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the Agent Governance Toolkit integration.
/// Bound from <c>AppConfig:AI:Governance</c> in appsettings.json.
/// </summary>
public sealed class GovernanceConfig
{
    /// <summary>Whether governance policy enforcement is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Whether per-invocation governance runs on the agent's live tool-call path. When true, every
    /// tool the agent calls during a turn passes through <c>IToolInvocationGovernor</c> (permission,
    /// graded-autonomy risk, capability, and policy checks) before executing, fail-closed. When false
    /// (the default) the governor is a pure pass-through and agent tool calls are not gated at
    /// invocation time — preserving existing behaviour for consumers who have not opted in.
    /// </summary>
    /// <remarks>
    /// This is the switch that connects the otherwise-dormant tool governance to the agent loop.
    /// Enabling it without configured permission rules makes the default "Ask" behaviour block tools,
    /// so operators should pair it with explicit allow rules. Independent of <see cref="Enabled"/>,
    /// which only gates the declarative YAML policy layer.
    /// </remarks>
    public bool EnforceToolInvocation { get; init; }

    /// <summary>
    /// Paths to YAML policy files. Relative paths resolve from the application base directory.
    /// </summary>
    public List<string> PolicyPaths { get; init; } = [];

    /// <summary>Strategy for resolving conflicts when multiple policy rules match.</summary>
    public ConflictResolutionStrategy ConflictStrategy { get; init; } = ConflictResolutionStrategy.PriorityFirstMatch;

    /// <summary>Whether deterministic prompt injection detection is enabled.</summary>
    public bool EnablePromptInjectionDetection { get; init; }

    /// <summary>
    /// Whether MCP tool security scanning is enabled on tool registration. When true, every tool
    /// definition discovered on an external MCP server is scanned for tool poisoning, hidden
    /// instructions, description injection and homoglyph typosquatting before it can be published to
    /// the model, and a finding at or above <see cref="McpToolBlockThreshold"/> withholds that tool.
    /// </summary>
    /// <remarks>
    /// Scanning happens at discovery rather than at call time because the attack surface is the tool's
    /// name, description and parameter schema — text the harness copies into the model's context so
    /// the model knows the tool exists. That text does its work the moment it is in context, whether
    /// or not the tool is ever invoked, so refusing the call later would be too late.
    /// </remarks>
    public bool EnableMcpSecurity { get; init; }

    /// <summary>Whether tamper-evident governance audit logging is enabled.</summary>
    public bool EnableAudit { get; init; } = true;

    /// <summary>Whether governance OTel metrics are emitted.</summary>
    public bool EnableMetrics { get; init; } = true;

    /// <summary>
    /// Minimum threat level that triggers blocking for prompt injection.
    /// Detections below this level are logged but not blocked.
    /// </summary>
    public ThreatLevel InjectionBlockThreshold { get; init; } = ThreatLevel.High;

    /// <summary>
    /// Minimum threat level on an MCP tool definition that withholds the tool from the model.
    /// Findings below this level are logged and counted, and the tool is still published.
    /// Only consulted when <see cref="EnableMcpSecurity"/> is true.
    /// </summary>
    /// <remarks>
    /// The default withholds instruction-override and role-injection findings while leaving the two
    /// lower-confidence heuristics — encoded blocks and unusual name characters — as reports.
    /// Raising this to <see cref="ThreatLevel.Critical"/> withholds only invisible-character
    /// findings, the narrowest rule; note it still carries one known false positive, since the
    /// zero-width non-joiner it looks for is load-bearing in Persian, Arabic and several Indic
    /// scripts.
    /// </remarks>
    public ThreatLevel McpToolBlockThreshold { get; init; } = ThreatLevel.High;

    /// <summary>Whether MCP tool response sanitization is enabled.</summary>
    public bool EnableResponseSanitization { get; init; } = true;

    /// <summary>
    /// Minimum threat level that triggers response blocking instead of redaction.
    /// Findings below this level are redacted and the sanitized response continues.
    /// </summary>
    public ThreatLevel ResponseBlockThreshold { get; init; } = ThreatLevel.Critical;

    /// <summary>
    /// Human escalation configuration for approval workflows triggered when
    /// agents exceed their authority.
    /// </summary>
    public EscalationConfig Escalation { get; init; } = new();

    /// <summary>
    /// Durable governance-state persistence (SQLite) for pending escalations and change
    /// proposals. Both toggles default to off — in-memory behavior is unchanged until a
    /// consumer opts in. See <see cref="GovernanceDurableStateConfig"/> for restart and
    /// consistency semantics.
    /// </summary>
    public GovernanceDurableStateConfig DurableState { get; init; } = new();

    /// <summary>
    /// Deterministic spin / no-progress guard for the agent's live tool-call path. Opt-in via
    /// <see cref="Governance.ProgressGuardConfig.Enabled"/>; off by default. Independent of
    /// <see cref="EnforceToolInvocation"/> — it answers "is the agent making progress?" rather than
    /// "may this tool run?".
    /// </summary>
    public ProgressGuardConfig ProgressGuard { get; init; } = new();

    /// <summary>
    /// Purview-backed data classification (classification-aware DLP) for the agent's live tool-call
    /// path. Opt-in via <see cref="Governance.DataClassificationConfig.Mode"/>; off by default. Resolves
    /// the Purview sensitivity label of the asset a tool is about to touch and allows / redacts / blocks
    /// the call accordingly — access control driven by classification metadata, distinct from the
    /// content-pattern response sanitizers.
    /// </summary>
    public DataClassificationConfig DataClassification { get; init; } = new();

    /// <summary>
    /// Routes an approval-required verdict on the agent's live tool-call path to the human
    /// escalation workflow instead of refusing the call. Opt-in via
    /// <see cref="Governance.ToolApprovalConfig.Enabled"/>; off by default, and additionally gated on
    /// <see cref="EscalationConfig.Enabled"/>. This is what makes <see cref="EnforceToolInvocation"/>'s
    /// "requires approval" outcome actually ask somebody — without it the outcome is recorded and
    /// the call is blocked, which is safe but silent.
    /// </summary>
    public ToolApprovalConfig ToolApproval { get; init; } = new();

    /// <summary>
    /// Gates tools by what they have declared they do rather than by name, requiring approval for
    /// anything not declared read-only. Opt-in via
    /// <see cref="Governance.ToolBehaviorGatingConfig.RequireApprovalForNonReadOnlyTools"/>; off by
    /// default, and inert without <see cref="EnforceToolInvocation"/> — which is why a host that
    /// enables one without the other fails validation rather than starting with a switch that does
    /// nothing.
    /// </summary>
    public ToolBehaviorGatingConfig ToolBehaviorGating { get; init; } = new();
}
