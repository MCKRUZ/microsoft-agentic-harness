namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Tuning for Polly v8 ratio-based circuit breaker applied to each LLM provider.
/// </summary>
public class CircuitBreakerConfig
{
    /// <summary>Failure ratio threshold (0 &lt; value &lt; 1) to trip the circuit.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Sliding window size in seconds for failure ratio sampling.</summary>
    public int SamplingDurationSeconds { get; set; } = 30;

    /// <summary>Minimum requests in the sampling window before the circuit evaluates.</summary>
    public int MinimumThroughput { get; set; } = 5;

    /// <summary>How long (in seconds) the circuit stays open before allowing a probe.</summary>
    public int BreakDurationSeconds { get; set; } = 60;
}
