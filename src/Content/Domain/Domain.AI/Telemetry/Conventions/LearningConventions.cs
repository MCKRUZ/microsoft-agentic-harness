namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OTel attribute names and metric identifiers for the learnings subsystem.
/// Used by command handlers to tag spans, structured logs, and metric recordings
/// with consistent, discoverable dimension keys.
/// </summary>
public static class LearningConventions
{
    // ── Attribute name constants (span/log attribute keys) ──

    /// <summary>Unique learning entry identifier.</summary>
    public const string LearningId = "agent.learning.id";

    /// <summary>The agent associated with the learning scope.</summary>
    public const string AgentId = "agent.learning.agent_id";

    /// <summary>Learning category classification.</summary>
    public const string Category = "agent.learning.category";

    /// <summary>Temporal decay class for the learning.</summary>
    public const string DecayClass = "agent.learning.decay_class";

    /// <summary>Learning visibility scope level.</summary>
    public const string Scope = "agent.learning.scope";

    // ── Metric identifier constants (instrument names) ──

    /// <summary>Counter of learnings captured via Remember. Tags: category, scope.</summary>
    public const string Remembered = "agent.learning.remembered";

    /// <summary>Counter of learnings retrieved via Recall.</summary>
    public const string Recalled = "agent.learning.recalled";

    /// <summary>Counter of learnings soft-deleted via Forget.</summary>
    public const string Forgotten = "agent.learning.forgotten";

    /// <summary>Counter of learnings feedback-improved via Improve.</summary>
    public const string Improved = "agent.learning.improved";

    /// <summary>Counter of expired learnings pruned by background service.</summary>
    public const string Pruned = "agent.learning.pruned";

    /// <summary>Histogram of recall pipeline duration in milliseconds.</summary>
    public const string RecallDurationMs = "agent.learning.recall_duration_ms";

    // ── Well-known tag value classes ──

    /// <summary>Well-known values for the <see cref="Category"/> attribute.</summary>
    public static class CategoryValues
    {
        /// <summary>Factual correction category.</summary>
        public const string FactualCorrection = "factual_correction";

        /// <summary>Style preference category.</summary>
        public const string StylePreference = "style_preference";

        /// <summary>Tool usage pattern category.</summary>
        public const string ToolUsagePattern = "tool_usage_pattern";

        /// <summary>Domain knowledge category.</summary>
        public const string DomainKnowledge = "domain_knowledge";

        /// <summary>Instruction update category.</summary>
        public const string InstructionUpdate = "instruction_update";
    }

    /// <summary>Well-known values for the <see cref="DecayClass"/> attribute.</summary>
    public static class DecayClassValues
    {
        /// <summary>Short-lived, fast-decaying knowledge.</summary>
        public const string Volatile = "volatile";

        /// <summary>Long-lived, slow-decaying knowledge.</summary>
        public const string Stable = "stable";

        /// <summary>Immortal knowledge that never decays.</summary>
        public const string Permanent = "permanent";
    }

    /// <summary>Well-known values for the <see cref="Scope"/> attribute.</summary>
    public static class ScopeValues
    {
        /// <summary>Scoped to a specific agent.</summary>
        public const string Agent = "agent";

        /// <summary>Scoped to a team of agents.</summary>
        public const string Team = "team";

        /// <summary>Visible to all agents.</summary>
        public const string Global = "global";
    }
}
