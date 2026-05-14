namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// OTel attribute names and metric identifiers for the resilience subsystem.
/// Covers circuit breaker state, fallback activations, retry tracking, and
/// retry queue metrics used by the provider health and fallback infrastructure.
/// </summary>
public static class ResilienceConventions
{
    // ── Attribute name constants (span/log attribute keys) ──

    /// <summary>Provider identifier for per-provider metric tagging.</summary>
    public const string ProviderName = "agent.resilience.provider";

    /// <summary>Circuit breaker state name (healthy, degraded, unavailable).</summary>
    public const string CircuitStateName = "agent.resilience.circuit.state_name";

    /// <summary>State transition source in circuit breaker state change events.</summary>
    public const string TransitionFrom = "agent.resilience.circuit.from";

    /// <summary>State transition target in circuit breaker state change events.</summary>
    public const string TransitionTo = "agent.resilience.circuit.to";

    /// <summary>Comma-separated list of provider names that failed during a fallback chain.</summary>
    public const string FailedProviders = "agent.resilience.failed_providers";

    // ── Metric identifier constants (instrument names) ──

    /// <summary>Counter of fallback provider activations. Tags: provider.</summary>
    public const string FallbackActivations = "agent.resilience.fallback.activations";

    /// <summary>Counter of circuit breaker state transitions. Tags: provider, from, to.</summary>
    public const string CircuitStateChanges = "agent.resilience.circuit.state_changes";

    /// <summary>Gauge of per-provider circuit state (0=healthy, 1=degraded, 2=unavailable). Tags: provider.</summary>
    public const string CircuitState = "agent.resilience.circuit.state";

    /// <summary>Counter of retry attempts per provider. Tags: provider.</summary>
    public const string RetryAttempts = "agent.resilience.retry.attempts";

    /// <summary>Histogram of per-provider request duration in milliseconds. Tags: provider.</summary>
    public const string ProviderDurationMs = "agent.resilience.provider.duration_ms";

    /// <summary>Counter of full provider exhaustion events (all providers failed).</summary>
    public const string DegradationEvents = "agent.resilience.degradation.events";

    /// <summary>Gauge of retry queue depth. Tags: provider.</summary>
    public const string QueueSize = "agent.resilience.queue.size";

    /// <summary>Counter of TTL-expired requests removed from the retry queue.</summary>
    public const string QueueExpired = "agent.resilience.queue.expired";

    // ── Well-known tag value classes ──

    /// <summary>Well-known values for the <see cref="CircuitStateName"/> attribute.</summary>
    public static class HealthValues
    {
        public const string Healthy = "healthy";
        public const string Degraded = "degraded";
        public const string Unavailable = "unavailable";
    }
}
