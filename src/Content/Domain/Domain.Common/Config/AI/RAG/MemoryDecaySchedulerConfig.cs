namespace Domain.Common.Config.AI.RAG;

/// <summary>
/// Configuration for the background scheduler that periodically applies memory decay and
/// prunes low-weight cross-session memories.
/// Bound from <c>AppConfig:AI:Rag:CrossSessionMemory:DecayScheduler</c>.
/// </summary>
public sealed class MemoryDecaySchedulerConfig
{
    /// <summary>
    /// Whether the background decay scheduler runs. Disabled by default so cloning the
    /// template does not silently start mutating stored memory weights — opt in explicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Interval between decay+prune passes. Default: 6 hours.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}
