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

    /// <summary>Whether MCP tool security scanning is enabled on tool registration.</summary>
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
}
