namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Retry policy tuning for transient LLM provider failures.
/// </summary>
public class RetryConfig
{
    /// <summary>Maximum attempts including the initial attempt.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Base delay in seconds for exponential backoff with jitter.</summary>
    public double BaseDelaySeconds { get; set; } = 1.0;

    /// <summary>Backoff type: "Exponential" or "Linear".</summary>
    public string BackoffType { get; set; } = "Exponential";
}
