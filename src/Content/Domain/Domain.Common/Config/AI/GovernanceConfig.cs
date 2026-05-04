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
}
