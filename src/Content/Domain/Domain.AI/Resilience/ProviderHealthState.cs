namespace Domain.AI.Resilience;

/// <summary>
/// Health state of an LLM provider, mapped from Polly circuit breaker states.
/// Numeric ordering enables <c>&gt;=</c> comparisons for severity checks.
/// </summary>
public enum ProviderHealthState
{
    /// <summary>Provider is accepting requests normally. Maps to circuit breaker Closed state.</summary>
    Healthy = 0,
    /// <summary>Provider is being probed for recovery. Maps to circuit breaker HalfOpen state.</summary>
    Degraded = 1,
    /// <summary>Provider is not accepting requests. Maps to circuit breaker Open or Isolated state.</summary>
    Unavailable = 2
}
