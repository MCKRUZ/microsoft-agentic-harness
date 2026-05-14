namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OpenTelemetry semantic conventions for drift detection metrics and traces.
/// </summary>
public static class DriftConventions
{
    public const string Scope = "agent.drift.scope";
    public const string ScopeIdentifier = "agent.drift.scope_identifier";
    public const string Severity = "agent.drift.severity";
    public const string Dimension = "agent.drift.dimension";

    public const string Evaluations = "agent.drift.evaluations";
    public const string EscalationsTriggered = "agent.drift.escalations_triggered";
    public const string BaselinesUpdated = "agent.drift.baselines_updated";
    public const string EvaluationDurationMs = "agent.drift.evaluation_duration_ms";

    /// <summary>Well-known tag values for <see cref="Severity"/>.</summary>
    public static class SeverityValues
    {
        public const string None = "none";
        public const string Warn = "warn";
        public const string Alert = "alert";
        public const string Escalate = "escalate";
    }

    /// <summary>Well-known tag values for <see cref="Scope"/>.</summary>
    public static class ScopeValues
    {
        public const string Agent = "agent";
        public const string Skill = "skill";
        public const string TaskType = "task_type";
    }
}
