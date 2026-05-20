namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Applies time-based EMA decay to cross-session memories and prunes those below threshold.
/// Decay formula: newWeight = weight * (1 - decayRate) ^ daysSinceLastAccess.
/// </summary>
public interface IMemoryDecayService
{
    /// <summary>Applies EMA decay to all memories based on LastAccessedAt.</summary>
    Task ApplyDecayAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes all memories with weight below the threshold.</summary>
    Task PruneAsync(double threshold, CancellationToken cancellationToken = default);
}
