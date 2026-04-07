using System.ComponentModel.DataAnnotations;

namespace Domain.Common.Config.Http.Policies;

/// <summary>
/// Configuration for the HTTP circuit breaker resilience policy.
/// Controls when the circuit opens after failures and how long it stays open.
/// </summary>
public class HttpCircuitBreakerPolicyConfig
{
    /// <summary>
    /// Gets or sets the duration the circuit stays open before attempting recovery.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    public TimeSpan DurationOfBreak { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the number of exceptions allowed before the circuit opens.
    /// </summary>
    /// <value>Default: 12.</value>
    [Range(0, int.MaxValue)]
    public int ExceptionsAllowedBeforeBreaking { get; set; } = 12;

    /// <summary>
    /// Gets or sets the failure ratio threshold that triggers the circuit breaker.
    /// </summary>
    /// <value>Default: 0.1 (10% failure rate).</value>
    [Range(0, 1)]
    public double FailureRatio { get; set; } = 0.1;
}
