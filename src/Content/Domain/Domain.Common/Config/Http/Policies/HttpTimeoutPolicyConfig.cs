using System.ComponentModel.DataAnnotations;

namespace Domain.Common.Config.Http.Policies;

/// <summary>
/// Configuration for the HTTP timeout resilience policy.
/// Controls the maximum duration allowed for HTTP operations.
/// </summary>
public class HttpTimeoutPolicyConfig
{
    /// <summary>
    /// Gets or sets the maximum timeout for HTTP operations.
    /// </summary>
    /// <value>Default: 30 seconds.</value>
    [Required]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
