using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking token usage per LLM call with agentic dimensions
/// (agent name, conversation ID, budget tracking). Supplements framework-native
/// <c>gen_ai.client.token.usage</c> which lacks agent-level context.
/// </summary>
public static class TokenUsageMetrics
{
    /// <summary>Input tokens per LLM call. Tags: agent.name, gen_ai.operation.name.</summary>
    public static Histogram<long> InputTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(TokenConventions.Input, "{token}", "Input tokens per LLM call");

    /// <summary>Output tokens per LLM call. Tags: agent.name, gen_ai.operation.name.</summary>
    public static Histogram<long> OutputTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(TokenConventions.Output, "{token}", "Output tokens per LLM call");

    /// <summary>Total tokens per LLM call. Tags: agent.name, gen_ai.operation.name.</summary>
    public static Histogram<long> TotalTokens { get; } =
        AppInstrument.Meter.CreateHistogram<long>(TokenConventions.Total, "{token}", "Total tokens per LLM call");

    /// <summary>Cumulative budget consumption. Tags: agent.name, agent.conversation.id.</summary>
    public static UpDownCounter<long> BudgetUsed { get; } =
        AppInstrument.Meter.CreateUpDownCounter<long>(TokenConventions.BudgetUsed, "{token}", "Cumulative token budget consumption");

    /// <summary>Cache-read tokens per LLM call (prompt cache hits). Tags: agent.name, gen_ai.request.model.</summary>
    public static Counter<long> CacheReadTokens { get; } =
        AppInstrument.Meter.CreateCounter<long>(TokenConventions.CacheRead, "{token}", "Tokens served from prompt cache");

    /// <summary>Cache-write tokens per LLM call (cache creation). Tags: agent.name, gen_ai.request.model.</summary>
    public static Counter<long> CacheWriteTokens { get; } =
        AppInstrument.Meter.CreateCounter<long>(TokenConventions.CacheWrite, "{token}", "Tokens written to prompt cache");

    /// <summary>Estimated cost in USD per LLM call. Tags: agent.name, gen_ai.request.model.</summary>
    public static Counter<double> CostEstimated { get; } =
        AppInstrument.Meter.CreateCounter<double>(TokenConventions.CostEstimated, "{usd}", "Estimated LLM cost in USD");

    /// <summary>Estimated cost savings from cache hits in USD. Tags: agent.name, gen_ai.request.model.</summary>
    public static Counter<double> CostCacheSavings { get; } =
        AppInstrument.Meter.CreateCounter<double>(TokenConventions.CostCacheSavings, "{usd}", "Estimated cost savings from prompt caching");

    /// <summary>Cache hit rate per LLM call (0-1). Tags: agent.name, gen_ai.request.model.</summary>
    public static Histogram<double> CacheHitRate { get; } =
        AppInstrument.Meter.CreateHistogram<double>(TokenConventions.CacheHitRate, "{ratio}", "Cache hit rate per LLM call");
}
