using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking LLM token usage, cache efficiency,
/// and estimated cost per call. Inspired by Nexus token analytics which tracks
/// per-model spend, cache savings, and cost-per-turn efficiency.
/// </summary>
/// <remarks>
/// <para>
/// These instruments are recorded by <c>LlmTokenTrackingProcessor</c> in
/// Infrastructure.Observability, which reads <c>gen_ai.usage.*</c> span
/// attributes and computes cost from <c>AppConfig.Observability.LlmPricing</c>.
/// </para>
/// <para>
/// Dimensions: <c>gen_ai.request.model</c>, <c>agent.name</c>,
/// <c>agent.conversation.id</c> (conversation_id only on counters to
/// avoid high-cardinality histogram explosion).
/// </para>
/// </remarks>
public static class LlmUsageMetrics
{
    /// <summary>Cache-read input tokens per LLM call. Tags: model, agent.</summary>
    public static Counter<long> CacheReadTokens { get; } =
        AppInstrument.Meter.CreateCounter<long>(TokenConventions.CacheRead, "{token}", "Cache-read input tokens per LLM call");

    /// <summary>Cache-write (creation) input tokens per LLM call. Tags: model, agent.</summary>
    public static Counter<long> CacheWriteTokens { get; } =
        AppInstrument.Meter.CreateCounter<long>(TokenConventions.CacheWrite, "{token}", "Cache-write input tokens per LLM call");

    /// <summary>Estimated cost in USD per LLM call. Tags: model, agent.</summary>
    public static Counter<double> EstimatedCost { get; } =
        AppInstrument.Meter.CreateCounter<double>(TokenConventions.CostEstimated, "{USD}", "Estimated cost per LLM call");

    /// <summary>Estimated cache savings in USD per LLM call. Tags: model, agent.</summary>
    public static Counter<double> CacheSavings { get; } =
        AppInstrument.Meter.CreateCounter<double>(TokenConventions.CostCacheSavings, "{USD}", "Estimated cost savings from cache hits");

    /// <summary>Cache hit rate per LLM call (0-1 ratio). Tags: model.</summary>
    public static Histogram<double> CacheHitRate { get; } =
        AppInstrument.Meter.CreateHistogram<double>(TokenConventions.CacheHitRate, "{ratio}", "Cache hit rate per LLM call");

    /// <summary>Estimated cost per conversation turn. Tags: agent.</summary>
    public static Histogram<double> CostPerTurn { get; } =
        AppInstrument.Meter.CreateHistogram<double>(TokenConventions.CostPerTurn, "{USD}", "Estimated cost per conversation turn");

    /// <summary>Total tokens per conversation turn. Tags: agent.</summary>
    public static Histogram<long> TokensPerTurn { get; } =
        AppInstrument.Meter.CreateHistogram<long>(TokenConventions.TokensPerTurn, "{token}", "Total tokens per conversation turn");
}
