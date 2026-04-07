using System.Diagnostics;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Token bucket rate limiter that drops spans exceeding the configured throughput.
/// Prevents a trace storm from overwhelming export backends.
/// </summary>
/// <remarks>
/// <para>
/// Uses a token bucket algorithm:
/// <list type="bullet">
///   <item><description>Tokens are added at <see cref="RateLimitingConfig.SpansPerSecond"/> rate</description></item>
///   <item><description>Each span consumes one token</description></item>
///   <item><description>Burst capacity is <c>SpansPerSecond * BurstMultiplier</c></description></item>
///   <item><description>When tokens are exhausted, spans are dropped until tokens refill</description></item>
/// </list>
/// </para>
/// <para>
/// Dropped spans are counted but not logged individually to avoid log amplification
/// during the exact scenario (high load) when logging capacity is most constrained.
/// A periodic summary is logged instead.
/// </para>
/// </remarks>
public sealed class RateLimitingProcessor : BaseProcessor<Activity>
{
    private readonly ILogger<RateLimitingProcessor> _logger;
    private readonly RateLimitingConfig _config;
    private readonly int _maxTokens;
    private readonly double _tokensPerMs;

    private double _availableTokens;
    private long _lastRefillTimestamp;
    private long _droppedSpanCount;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitingProcessor"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="appConfig">The application configuration containing rate limiting settings.</param>
    public RateLimitingProcessor(
        ILogger<RateLimitingProcessor> logger,
        IOptions<Domain.Common.Config.AppConfig> appConfig)
    {
        _logger = logger;
        _config = appConfig.Value.Observability.RateLimiting;

        if (_config.SpansPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(appConfig), "SpansPerSecond must be positive");
        if (_config.BurstMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(appConfig), "BurstMultiplier must be positive");

        _maxTokens = _config.SpansPerSecond * _config.BurstMultiplier;
        _tokensPerMs = _config.SpansPerSecond / 1000.0;
        _availableTokens = _maxTokens; // Start full
        _lastRefillTimestamp = Stopwatch.GetTimestamp();

        _logger.LogInformation(
            "Rate limiting initialized: {SpansPerSecond} spans/sec, burst={BurstCapacity}",
            _config.SpansPerSecond,
            _maxTokens);
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (!_config.Enabled)
            return;

        if (!TryConsumeToken())
        {
            // Span is dropped — count it but don't log each one
            var dropped = Interlocked.Increment(ref _droppedSpanCount);

            // Log summary every 1000 dropped spans
            if (dropped % 1000 == 0)
            {
                _logger.LogWarning(
                    "Rate limiter has dropped {DroppedCount} spans total (limit: {Limit}/sec)",
                    dropped,
                    _config.SpansPerSecond);
            }

            // Signal the SDK to drop this span by setting it to a non-recorded status
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }

    private bool TryConsumeToken()
    {
        lock (_lock)
        {
            RefillTokens();

            if (_availableTokens >= 1.0)
            {
                _availableTokens -= 1.0;
                return true;
            }

            return false;
        }
    }

    private void RefillTokens()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastRefillTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastRefillTimestamp = now;

        _availableTokens = Math.Min(
            _maxTokens,
            _availableTokens + (elapsedMs * _tokensPerMs));
    }
}
