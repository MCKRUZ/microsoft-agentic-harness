namespace Domain.AI.Compaction;

/// <summary>
/// Identifies what triggered a compaction event.
/// </summary>
public enum CompactionTrigger
{
    /// <summary>Triggered automatically when token budget threshold was exceeded.</summary>
    AutoBudget,

    /// <summary>Triggered manually by user or agent request.</summary>
    Manual,

    /// <summary>Triggered by circuit breaker recovery after previous failures.</summary>
    CircuitBreaker
}
