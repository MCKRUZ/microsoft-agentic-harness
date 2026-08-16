using Domain.Common.Config.AI.Governance;

namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the Agent Governance Toolkit integration.
/// Bound from <c>AppConfig:AI:Governance</c> in appsettings.json.
/// </summary>
public sealed class GovernanceConfig
{
    /// <summary>
    /// Whether the declarative YAML policy layer (<see cref="PolicyPaths"/>, <c>IGovernancePolicyEngine</c>)
    /// is loaded and consulted.
    /// </summary>
    /// <remarks>
    /// Governs the policy layer specifically, not the whole Agent Governance Toolkit integration
    /// (#386). <see cref="EnablePromptInjectionDetection"/> and <see cref="EnableMcpSecurity"/> are
    /// independent switches: each stands up the AGT kernel on its own, even when this is
    /// <see langword="false"/>, so turning off the policy layer does not silently turn off
    /// injection detection or MCP tool scanning. The composition root
    /// (<c>Presentation.Common.IServiceCollectionExtensions</c>,
    /// <c>Infrastructure.AI.MCPServer.Program</c>) wires the AGT kernel whenever any of the three
    /// is <see langword="true"/>; when this flag alone is <see langword="false"/>,
    /// <c>IGovernancePolicyEngine</c> resolves the no-op engine and <see cref="PolicyPaths"/> is
    /// never read.
    /// </remarks>
    public bool Enabled { get; init; }

    /// <summary>
    /// Whether the composition root should stand up the AGT kernel — <c>true</c> when any of
    /// <see cref="Enabled"/>, <see cref="EnablePromptInjectionDetection"/>,
    /// <see cref="EnableMcpSecurity"/>, <see cref="EnableResponseSanitization"/>, or
    /// <see cref="Governance.DataClassificationConfig.Mode"/> being on is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single decision behind <c>AddGovernanceDependencies</c> vs <c>AddGovernanceNoOpDependencies</c>
    /// (#386), computed once here so <c>Presentation.Common.IServiceCollectionExtensions</c> and
    /// <c>Infrastructure.AI.MCPServer.Program</c> — the two composition roots that each make this call —
    /// cannot drift apart on what "any of these" means. A future governance sub-flag that should
    /// also arm the kernel is added to this one expression, not copied into two files.
    /// </para>
    /// <para>
    /// <strong><see cref="EnableResponseSanitization"/> and <see cref="DataClassification"/> must both
    /// be included here</strong>, found during this PR's own security review. <c>AddDataClassificationProvider</c>
    /// — the only code that reads <see cref="DataClassification"/> to decide between the real Purview
    /// routing provider and <c>NoOpDataClassificationProvider</c> — only runs inside
    /// <c>AddGovernanceDependencies</c>; omitting it here means an operator who configures
    /// <c>DataClassification.Mode = Enforce</c> with Purview wired, but leaves the other four flags at
    /// their defaults, gets a DLP gate that runs, looks armed, and silently allows everything — the
    /// same inversion this whole property exists to prevent, one layer down. Both flags also default
    /// away from the other three: <see cref="EnableResponseSanitization"/> defaults
    /// <see langword="true"/>, and <see cref="Governance.DataClassificationConfig.Mode"/> defaults
    /// <see cref="Governance.ClassificationEnforcementMode.Off"/>, which is why the check below is
    /// <c>!= Off</c> rather than a bare flag read. The checked-in appsettings.json files all set
    /// <see cref="Enabled"/> explicitly, which masked both gaps for every shipped host.
    /// </para>
    /// </remarks>
    public bool ArmsAgtKernel =>
        Enabled
        || EnablePromptInjectionDetection
        || EnableMcpSecurity
        || EnableResponseSanitization
        || DataClassification.Mode != Governance.ClassificationEnforcementMode.Off;

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

    /// <summary>
    /// Whether deterministic prompt injection detection is enabled. Independent of
    /// <see cref="Enabled"/> (#386) — stands up the AGT kernel and the real scanner on its own, even
    /// when the declarative policy layer is off.
    /// </summary>
    public bool EnablePromptInjectionDetection { get; init; }

    /// <summary>
    /// Whether MCP tool security scanning is enabled on tool registration. When true, every tool
    /// definition discovered on an external MCP server is scanned for tool poisoning, hidden
    /// instructions, description injection and homoglyph typosquatting before it can be published to
    /// the model, and a finding at or above <see cref="McpToolBlockThreshold"/> withholds that tool.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Enabled"/> (#386) — stands up the AGT kernel and the real scanner on
    /// its own, even when the declarative policy layer is off.
    /// <para>
    /// Scanning happens at discovery rather than at call time because the attack surface is the tool's
    /// name, description and parameter schema — text the harness copies into the model's context so
    /// the model knows the tool exists. That text does its work the moment it is in context, whether
    /// or not the tool is ever invoked, so refusing the call later would be too late.
    /// </para>
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
    /// <para>
    /// <strong>Also gates the cross-server structural scan</strong> (tool-name collision, shadowing,
    /// definition drift — see <c>ToolChainBuilder.Surface.cs</c>'s <c>ApplySurfaceFindings</c>), which
    /// shares this one threshold by design rather than adding a second knob an operator would have to
    /// remember to tune in step. Collision is always withheld regardless of this setting. Shadowing and
    /// drift are fixed at <see cref="ThreatLevel.High"/>, so raising this threshold to
    /// <see cref="ThreatLevel.Critical"/> — narrowing the per-tool content scanner to
    /// invisible-character findings only, as described above — also stops shadowing and drift from
    /// being withheld (they still log and count). That is an intentional consequence of sharing the
    /// knob, not a gap: an operator narrowing this threshold has explicitly chosen to trust more and
    /// block less across every finding type it gates.
    /// </para>
    /// </remarks>
    public ThreatLevel McpToolBlockThreshold { get; init; } = ThreatLevel.High;

    /// <summary>
    /// Whether tool response sanitization (credential redaction, injection scrubbing, exfiltration URL
    /// detection) is enabled. Defaults to <see langword="true"/>, unlike its sibling flags — see
    /// <see cref="ArmsAgtKernel"/>'s remarks for why that default is exactly why this flag must
    /// participate in arming the AGT kernel, not merely gate the behaviour that consumes it.
    /// </summary>
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

    /// <summary>
    /// Governs tool <em>combinations</em> — an untrusted-input tool co-resident with a high-impact sink
    /// — rather than individual tools. Every pairing defaults to
    /// <see cref="CompositionPosture.Allow"/>, so a host that configures nothing here reports
    /// and enforces nothing; a pairing set to <see cref="CompositionPosture.RequireApproval"/>
    /// is, like <see cref="ToolBehaviorGating"/>, inert without <see cref="EnforceToolInvocation"/>.
    /// </summary>
    public ToolCompositionGatingConfig ToolCompositionGating { get; init; } = new();

    /// <summary>
    /// Posture for the MCP tool surface scan — collision, shadowing, and definition drift — layered
    /// on top of the per-tool content rules. Gated by <see cref="EnableMcpSecurity"/>, the same flag
    /// that gates the per-tool scanner, rather than a second switch: a control an operator believes is
    /// on because a related-looking flag is on has shipped as a defect before.
    /// </summary>
    public McpToolSurfaceScanningConfig McpToolSurfaceScanning { get; init; } = new();
}
