namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OpenTelemetry semantic conventions for the context compaction system.
/// </summary>
public static class CompactionConventions
{
    /// <summary>The compaction strategy used (Full, Partial, Micro).</summary>
    public const string Strategy = "agent.compaction.strategy";

    /// <summary>The trigger that initiated compaction.</summary>
    public const string Trigger = "agent.compaction.trigger";

    /// <summary>Tokens saved by the compaction operation.</summary>
    public const string TokensSaved = "agent.compaction.tokens_saved";

    /// <summary>Duration of the compaction operation in milliseconds.</summary>
    public const string Duration = "agent.compaction.duration_ms";

    /// <summary>Whether the auto-compact circuit breaker is tripped.</summary>
    public const string CircuitBroken = "agent.compaction.circuit_broken";
}
