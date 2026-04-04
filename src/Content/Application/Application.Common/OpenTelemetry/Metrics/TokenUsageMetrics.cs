using System.Diagnostics.Metrics;
using Application.Common.OpenTelemetry.Conventions;
using Application.Common.OpenTelemetry.Instruments;

namespace Application.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking token usage per LLM call with agentic dimensions
/// (agent name, conversation ID, budget tracking). Supplements framework-native
/// <c>gen_ai.client.token.usage</c> which lacks agent-level context.
/// </summary>
public static class TokenUsageMetrics
{
    /// <summary>Input tokens per LLM call. Tags: agent.name, gen_ai.operation.name.</summary>
    public static Histogram<long> InputTokens { get; } =
        AgenticHarnessInstrument.Meter.CreateHistogram<long>(AgenticSemanticConventions.Tokens.Input, "{token}", "Input tokens per LLM call");

    /// <summary>Output tokens per LLM call. Tags: agent.name, gen_ai.operation.name.</summary>
    public static Histogram<long> OutputTokens { get; } =
        AgenticHarnessInstrument.Meter.CreateHistogram<long>(AgenticSemanticConventions.Tokens.Output, "{token}", "Output tokens per LLM call");

    /// <summary>Total tokens per LLM call. Tags: agent.name, gen_ai.operation.name.</summary>
    public static Histogram<long> TotalTokens { get; } =
        AgenticHarnessInstrument.Meter.CreateHistogram<long>(AgenticSemanticConventions.Tokens.Total, "{token}", "Total tokens per LLM call");

    /// <summary>Cumulative budget consumption. Tags: agent.name, agent.conversation.id.</summary>
    public static UpDownCounter<long> BudgetUsed { get; } =
        AgenticHarnessInstrument.Meter.CreateUpDownCounter<long>(AgenticSemanticConventions.Tokens.BudgetUsed, "{token}", "Cumulative token budget consumption");
}
