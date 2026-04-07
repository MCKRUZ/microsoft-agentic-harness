using System.ComponentModel.DataAnnotations;

namespace Domain.Common.Config.Http.Policies;

/// <summary>
/// Configuration for the HTTP retry resilience policy.
/// Controls exponential backoff parameters and retry count.
/// </summary>
public class HttpRetryPolicyConfig
{
    /// <summary>
    /// Gets or sets the backoff power for exponential delay calculation.
    /// </summary>
    /// <value>Default: 2 (standard exponential backoff).</value>
    [Required, Range(0, int.MaxValue)]
    public int BackoffPower { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of retry attempts.
    /// </summary>
    /// <value>Default: 3.</value>
    [Required, Range(0, int.MaxValue)]
    public int Count { get; set; } = 3;

    /// <summary>
    /// Gets or sets the base delay between retries before backoff is applied.
    /// </summary>
    /// <value>Default: 2 seconds.</value>
    [Required]
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);
}
