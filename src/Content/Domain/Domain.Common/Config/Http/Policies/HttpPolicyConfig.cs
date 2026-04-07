namespace Domain.Common.Config.Http.Policies;

/// <summary>
/// Aggregate configuration for all HTTP resilience policies.
/// Groups circuit breaker, retry, and timeout settings.
/// </summary>
/// <remarks>
/// Binds to the <c>AppConfig:Http:Policies</c> section in appsettings.json.
/// </remarks>
public class HttpPolicyConfig
{
    /// <summary>
    /// Gets or sets the circuit breaker policy configuration.
    /// </summary>
    public HttpCircuitBreakerPolicyConfig HttpCircuitBreaker { get; set; } = new();

    /// <summary>
    /// Gets or sets the retry policy configuration.
    /// </summary>
    public HttpRetryPolicyConfig HttpRetry { get; set; } = new();

    /// <summary>
    /// Gets or sets the timeout policy configuration.
    /// </summary>
    public HttpTimeoutPolicyConfig HttpTimeout { get; set; } = new();
}
