using System.ComponentModel.DataAnnotations;

namespace Domain.Common.Config.Http.Policies;

/// <summary>
/// Configuration for the HTTP timeout resilience policy.
/// Controls the per-attempt and total durations allowed for HTTP operations.
/// </summary>
public class HttpTimeoutPolicyConfig
{
    /// <summary>
    /// Gets or sets the PER-ATTEMPT timeout: each individual HTTP send (including each retry
    /// attempt) is cancelled after this duration. The whole operation across all retries is
    /// bounded by <see cref="TotalTimeout"/>, not by this value — <c>HttpClient.Timeout</c> is
    /// set to infinite so it cannot race the pipeline and truncate the retry budget.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    [Required]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the TOTAL timeout for an HTTP operation across all retry attempts and
    /// backoff delays. When <see langword="null"/> (the default) it is computed as
    /// <c>(HttpRetry.Count + 1) × Timeout</c> plus exponential-backoff headroom, so the retry
    /// budget always fits inside the total budget.
    /// </summary>
    /// <value>Default: <see langword="null"/> (computed from retry count, per-attempt timeout, and backoff).</value>
    public TimeSpan? TotalTimeout { get; set; }
}
