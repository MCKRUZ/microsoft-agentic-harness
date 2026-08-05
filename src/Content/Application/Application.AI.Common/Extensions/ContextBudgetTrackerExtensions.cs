using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Records a context-budget allocation and publishes the telemetry that must always accompany it.
/// </summary>
/// <remarks>
/// Charging the budget and reporting the charge are one act, not two. Every component owes three writes:
/// the allocation itself, a sample on its own component instrument, and a sample on the shared
/// <see cref="ContextSourceMetrics.SourceTokens"/> histogram that the Context Explorer dashboard reads.
/// Written out by hand at each site, the third one is the one that gets forgotten — and its absence is
/// silent, because the budget number stays correct while the dashboard simply stops showing that slice.
/// This exists so the set cannot be written incompletely.
/// </remarks>
public static class ContextBudgetTrackerExtensions
{
    /// <summary>
    /// Charges <paramref name="tokens"/> to a named component of <paramref name="agentName"/>'s budget and
    /// publishes the component and context-source metrics for it.
    /// </summary>
    /// <param name="tracker">The budget being charged.</param>
    /// <param name="agentName">The agent whose budget this belongs to.</param>
    /// <param name="component">The budget component, from <see cref="ContextConventions.BudgetComponents"/>.</param>
    /// <param name="sourceType">
    /// The context source this counts as, from <see cref="ContextConventions.SourceTypeValues"/>. Several
    /// components can share one source type — the two skill tiers are both <c>skills</c> — so the dashboard
    /// can total a source without losing the finer breakdown.
    /// </param>
    /// <param name="tokens">The estimated token cost.</param>
    /// <param name="componentInstrument">The histogram specific to this component.</param>
    /// <param name="extraTag">
    /// An optional dimension for <paramref name="componentInstrument"/> only — the skill tier, for instance.
    /// It is deliberately not applied to the shared source histogram, whose dimensions must stay uniform
    /// across every component that writes to it.
    /// </param>
    public static void RecordAndPublish(
        this IContextBudgetTracker tracker,
        string agentName,
        string component,
        string sourceType,
        int tokens,
        Histogram<long> componentInstrument,
        KeyValuePair<string, object?>? extraTag = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(componentInstrument);

        tracker.RecordAllocation(agentName, component, tokens);

        var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, agentName);

        if (extraTag is { } tag)
            componentInstrument.Record(tokens, agentTag, tag);
        else
            componentInstrument.Record(tokens, agentTag);

        ContextSourceMetrics.SourceTokens.Record(tokens,
            new KeyValuePair<string, object?>(ContextConventions.SourceType, sourceType),
            agentTag);
    }
}
