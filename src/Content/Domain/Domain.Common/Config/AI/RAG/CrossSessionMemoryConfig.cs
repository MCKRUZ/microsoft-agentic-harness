namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for cross-session memory persistence with EMA decay.
/// Bound from AppConfig:AI:Rag:CrossSessionMemory.
/// </summary>
public sealed class CrossSessionMemoryConfig
{
    /// <summary>Whether cross-session memory is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// EMA decay rate per day. Higher values cause faster forgetting. Default: 0.05.
    /// </summary>
    public double DecayRate { get; set; } = 0.05;

    /// <summary>Weight threshold below which memories are pruned. Default: 0.01.</summary>
    public double PruneThreshold { get; set; } = 0.01;

    /// <summary>Maximum number of memories to retain. Default: 10,000.</summary>
    public int MaxMemories { get; set; } = 10_000;

    /// <summary>Interval between background syncs to graph backend. Default: 5 minutes.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Background decay scheduler settings. Disabled by default (opt-in) — when enabled a
    /// hosted service periodically applies decay and prunes low-weight memories.
    /// </summary>
    public MemoryDecaySchedulerConfig DecayScheduler { get; set; } = new();
}
