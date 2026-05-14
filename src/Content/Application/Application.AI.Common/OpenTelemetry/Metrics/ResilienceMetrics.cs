using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking provider resilience — circuit breaker state,
/// fallback activations, retry attempts, and retry queue health.
/// </summary>
/// <remarks>
/// <para>Recorded by multiple downstream sections:</para>
/// <list type="bullet">
///   <item><c>ProviderResiliencePipelineBuilder</c> (Section 11) — retry attempts, provider duration</item>
///   <item><c>ResilientChatClient</c> (Section 12) — fallback activations, degradation events</item>
///   <item><c>PollyProviderHealthMonitor</c> (Section 13) — circuit state changes, circuit state gauge</item>
///   <item><c>LlmRetryQueue</c> (Section 15) — queue size, queue expired</item>
/// </list>
/// </remarks>
public static class ResilienceMetrics
{
    /// <summary>Fallback provider activations. Tags: provider.</summary>
    public static Counter<long> FallbackActivations { get; } =
        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.FallbackActivations, "{activation}", "Fallback provider activations");

    /// <summary>Circuit breaker state transitions. Tags: provider, from, to.</summary>
    public static Counter<long> CircuitStateChanges { get; } =
        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.CircuitStateChanges, "{change}", "Circuit breaker state transitions");

    /// <summary>Per-provider circuit state gauge (0=healthy, 1=degraded, 2=unavailable). Tags: provider.</summary>
    /// <remarks>
    /// Recording sites must track the previous state value and record the delta
    /// (newState - previousState) to maintain correct gauge semantics.
    /// </remarks>
    public static UpDownCounter<long> CircuitState { get; } =
        AppInstrument.Meter.CreateUpDownCounter<long>(ResilienceConventions.CircuitState, "{state}", "Per-provider circuit state gauge");

    /// <summary>Retry attempts per provider. Tags: provider.</summary>
    public static Counter<long> RetryAttempts { get; } =
        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.RetryAttempts, "{attempt}", "Retry attempts per provider");

    /// <summary>Per-provider request duration. Tags: provider.</summary>
    public static Histogram<double> ProviderDurationMs { get; } =
        AppInstrument.Meter.CreateHistogram<double>(ResilienceConventions.ProviderDurationMs, "ms", "Per-provider request duration");

    /// <summary>Full provider exhaustion events (all providers failed).</summary>
    public static Counter<long> DegradationEvents { get; } =
        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.DegradationEvents, "{event}", "Full provider exhaustion events");

    /// <summary>Retry queue depth gauge. Tags: provider.</summary>
    public static UpDownCounter<long> QueueSize { get; } =
        AppInstrument.Meter.CreateUpDownCounter<long>(ResilienceConventions.QueueSize, "{request}", "Retry queue depth gauge");

    /// <summary>TTL-expired requests removed from the retry queue.</summary>
    public static Counter<long> QueueExpired { get; } =
        AppInstrument.Meter.CreateCounter<long>(ResilienceConventions.QueueExpired, "{expiry}", "TTL-expired queued requests");
}
