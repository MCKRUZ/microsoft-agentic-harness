namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Configuration for the retry queue and degraded mode behavior
/// when all LLM providers are exhausted.
/// </summary>
public class DegradedModeConfig
{
    /// <summary>How long (in seconds) queued requests survive before TTL expiry.</summary>
    public int RetryQueueTtlSeconds { get; set; } = 300;

    /// <summary>Maximum items in the retry queue.</summary>
    public int MaxQueueSize { get; set; } = 100;
}
