namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for span rate limiting. Uses a token bucket algorithm to
/// cap throughput and prevent a runaway trace storm from overwhelming backends.
/// </summary>
public class RateLimitingConfig
{
    /// <summary>
    /// Gets or sets whether rate limiting is enabled.
    /// When disabled, all spans pass through regardless of throughput.
    /// </summary>
    /// <value>Default: true.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of spans allowed per second.
    /// Spans exceeding this rate are dropped before export.
    /// </summary>
    /// <value>Default: 100 spans/second.</value>
    public int SpansPerSecond { get; set; } = 100;

    /// <summary>
    /// Gets or sets the burst capacity as a multiplier of <see cref="SpansPerSecond"/>.
    /// Allows temporary spikes above the sustained rate.
    /// </summary>
    /// <value>Default: 2 (allows bursts up to 2x the sustained rate).</value>
    public int BurstMultiplier { get; set; } = 2;
}
