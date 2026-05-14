namespace Domain.Common.Config.AI.Resilience;

/// <summary>
/// Root configuration for LLM provider resilience including fallback chains,
/// circuit breakers, retry policies, and degraded mode behavior.
/// Bound from <c>AppConfig:AI:Resilience</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.Resilience
/// ├── Enabled          — Master toggle for resilience
/// ├── FallbackChain[]  — Ordered provider entries with capabilities
/// │   ├── ClientType       — Provider SDK (AzureOpenAI, OpenAI, etc.)
/// │   ├── DeploymentId     — Model deployment name
/// │   └── Capabilities     — Feature declarations (tool calling, streaming, vision)
/// ├── CircuitBreaker   — Failure ratio, sampling, break duration
/// ├── Retry            — Max attempts, backoff
/// ├── Timeout          — Per-attempt timeout
/// └── DegradedMode     — Retry queue TTL and max size
/// </code>
/// </para>
/// </remarks>
public class ResilienceConfig
{
    /// <summary>
    /// Master toggle. When disabled, <c>ResilientChatClientProvider</c> returns
    /// the primary provider's raw client and <c>LlmRetryQueue</c> is not registered.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Ordered list of LLM providers. First entry is primary; rest are fallbacks
    /// activated in order when the primary is unavailable.
    /// </summary>
    public FallbackProviderConfig[] FallbackChain { get; set; } = [];

    /// <summary>Circuit breaker tuning for Polly v8 ratio-based circuit breaker.</summary>
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new();

    /// <summary>Retry policy tuning for transient failure handling.</summary>
    public RetryConfig Retry { get; set; } = new();

    /// <summary>Per-attempt timeout configuration.</summary>
    public TimeoutConfig Timeout { get; set; } = new();

    /// <summary>Retry queue and degraded mode behavior when all providers are exhausted.</summary>
    public DegradedModeConfig DegradedMode { get; set; } = new();
}
