namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Per-attempt timeout configuration for LLM provider calls.
/// </summary>
public class TimeoutConfig
{
    /// <summary>Timeout in seconds for each individual provider call attempt.</summary>
    public int PerAttemptSeconds { get; set; } = 30;
}
