using System.Collections.Concurrent;
using System.Diagnostics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Tail-based sampling processor that buffers spans by trace ID and evaluates
/// the complete trace before deciding whether to keep or drop it.
/// </summary>
/// <remarks>
/// <para>
/// Unlike head-based sampling (decided at trace start with incomplete info),
/// tail-based sampling waits for the trace to complete, then applies policies:
/// </para>
/// <list type="number">
///   <item><description>Errors — always keep traces containing error spans</description></item>
///   <item><description>Slow — keep traces exceeding the duration threshold</description></item>
///   <item><description>Agent — keep traces with AI agent execution attributes</description></item>
///   <item><description>Probabilistic — sample a percentage of remaining traces</description></item>
/// </list>
/// <para>
/// Spans are buffered in <see cref="_traceBuffers"/> keyed by trace ID.
/// A background timer periodically evaluates aged-out traces (older than
/// <see cref="SamplingConfig.DecisionWait"/>) and flushes kept spans to
/// the next processor in the pipeline.
/// </para>
/// </remarks>
public sealed class TailBasedSamplingProcessor : BaseProcessor<Activity>
{
    private readonly ILogger<TailBasedSamplingProcessor> _logger;
    private readonly SamplingConfig _config;
    private readonly ConcurrentDictionary<string, TraceBuffer> _traceBuffers = new();
    private readonly Timer _evaluationTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="TailBasedSamplingProcessor"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="appConfig">The application configuration containing sampling settings.</param>
    public TailBasedSamplingProcessor(
        ILogger<TailBasedSamplingProcessor> logger,
        IOptions<Domain.Common.Config.AppConfig> appConfig)
    {
        _logger = logger;
        _config = appConfig.Value.Observability.Sampling;

        if (_config.MaxBufferedTraces <= 0)
            throw new ArgumentOutOfRangeException(nameof(appConfig), "MaxBufferedTraces must be positive");
        if (_config.DefaultSamplingPercentage is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(appConfig), "DefaultSamplingPercentage must be 0-100");

        var evaluationInterval = _config.DecisionWait / 2;
        _evaluationTimer = new Timer(
            _ => EvaluateBufferedTraces(),
            null,
            evaluationInterval,
            evaluationInterval);

        _logger.LogInformation(
            "Tail-based sampling initialized: DecisionWait={DecisionWait}s, " +
            "MaxBuffered={MaxBuffered}, SampleRate={SampleRate}%",
            _config.DecisionWait.TotalSeconds,
            _config.MaxBufferedTraces,
            _config.DefaultSamplingPercentage);
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (!_config.Enabled)
            return;

        var traceId = data.TraceId.ToString();
        var buffer = _traceBuffers.GetOrAdd(traceId, _ => new TraceBuffer());
        buffer.Add(data);
    }

    private void EvaluateBufferedTraces()
    {
        // Evict overflow first (moved from OnEnd to avoid O(n log n) on hot path)
        EvictOverflow();

        var cutoff = DateTimeOffset.UtcNow - _config.DecisionWait;

        foreach (var kvp in _traceBuffers)
        {
            if (kvp.Value.OldestTimestamp > cutoff)
                continue;

            if (!_traceBuffers.TryRemove(kvp.Key, out var buffer))
                continue;

            var spans = buffer.Spans;
            var decision = EvaluateSamplingDecision(spans);

            if (decision)
            {
                foreach (var span in spans)
                {
                    base.OnEnd(span);
                }

                _logger.LogDebug("Trace {TraceId} KEPT ({SpanCount} spans)", kvp.Key, spans.Count);
            }
            else
            {
                foreach (var span in spans)
                {
                    span.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                }

                _logger.LogDebug("Trace {TraceId} DROPPED by tail-based sampling", kvp.Key);
            }
        }
    }

    private bool EvaluateSamplingDecision(List<Activity> spans)
    {
        foreach (var span in spans)
        {
            if (_config.AlwaysKeepErrors && span.Status == ActivityStatusCode.Error)
                return true;

            if (span.Duration.TotalMilliseconds > _config.SlowRequestThresholdMs)
                return true;

            if (_config.AlwaysKeepAgentExecutions && IsAgentExecution(span))
                return true;
        }

        // Probabilistic sampling (Random.Shared is thread-safe in .NET 6+)
        return Random.Shared.NextDouble() * 100 < _config.DefaultSamplingPercentage;
    }

    private static bool IsAgentExecution(Activity activity)
    {
        if (activity.GetTagItem(AgentConventions.Phase) is not null)
            return true;

        if (activity.GetTagItem(AgentConventions.GenAiSystem) is string system)
        {
            return system.Equals(AgentConventions.GenAiSystemSemanticKernel, StringComparison.OrdinalIgnoreCase)
                || system.Equals(AgentConventions.GenAiSystemExtensionsAI, StringComparison.OrdinalIgnoreCase)
                || system.Equals(AgentConventions.GenAiSystemAgentsAI, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void EvictOverflow()
    {
        var excess = _traceBuffers.Count - _config.MaxBufferedTraces;
        if (excess <= 0)
            return;

        // Evict oldest traces — O(n) scan instead of O(n log n) sort
        var oldest = new List<(string Key, DateTimeOffset Timestamp)>(excess + 1);
        foreach (var kvp in _traceBuffers)
        {
            oldest.Add((kvp.Key, kvp.Value.OldestTimestamp));
        }

        oldest.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        for (var i = 0; i < excess && i < oldest.Count; i++)
        {
            _traceBuffers.TryRemove(oldest[i].Key, out _);
            _logger.LogDebug("Evicted trace {TraceId} due to buffer overflow", oldest[i].Key);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _evaluationTimer.Dispose();
            _traceBuffers.Clear();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Buffers spans belonging to a single trace for deferred sampling evaluation.
    /// </summary>
    private sealed class TraceBuffer
    {
        private readonly List<Activity> _spans = [];
        private readonly object _lock = new();

        /// <summary>
        /// Gets the timestamp of the oldest span in the buffer.
        /// </summary>
        public DateTimeOffset OldestTimestamp { get; private set; } = DateTimeOffset.MaxValue;

        /// <summary>
        /// Gets a snapshot of the buffered spans.
        /// </summary>
        public List<Activity> Spans
        {
            get
            {
                lock (_lock)
                {
                    return [.. _spans];
                }
            }
        }

        /// <summary>
        /// Adds a span to the trace buffer.
        /// </summary>
        /// <param name="activity">The span to buffer.</param>
        public void Add(Activity activity)
        {
            lock (_lock)
            {
                _spans.Add(activity);
                var startTime = activity.StartTimeUtc;
                if (startTime < OldestTimestamp.UtcDateTime)
                    OldestTimestamp = startTime;
            }
        }
    }
}
