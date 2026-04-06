using System.Diagnostics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;
using OpenTelemetry;

namespace Application.AI.Common.OpenTelemetry.Processors;

/// <summary>
/// Enriches AI framework spans with conversation-level context (conversation ID,
/// turn index, agent name) from <see cref="Activity.Baggage"/>. The AI frameworks
/// emit <c>gen_ai.chat</c> spans but have no concept of multi-turn conversations
/// or agent identity — this processor adds those dimensions for trace correlation.
/// </summary>
/// <remarks>
/// <para>
/// The orchestration harness sets baggage at conversation start:
/// <c>Activity.Current?.SetBaggage("agent.conversation.id", conversationId)</c>.
/// This processor reads baggage and copies it to span tags so it appears in
/// Jaeger, Azure Monitor, and other trace backends.
/// </para>
/// <para>
/// Processes activities from harness sources (exact match) and AI framework
/// sources (substring match on Microsoft.Agents.AI, Microsoft.Extensions.AI,
/// Microsoft.SemanticKernel) to avoid tagging unrelated spans.
/// </para>
/// </remarks>
public sealed class ConversationSpanProcessor : BaseProcessor<Activity>
{
    // Exact match uses Ordinal (our source names are known constants).
    // Substring match uses OrdinalIgnoreCase (SDK source names may vary in casing).
    private static readonly HashSet<string> ExactSources =
    [
        AppSourceNames.AgenticHarness,
        AppSourceNames.AgenticHarnessMediatR
    ];

    private static readonly string[] BaggageKeys =
    [
        AgentConventions.ConversationId,
        AgentConventions.TurnIndex,
        AgentConventions.Name,
    ];

    /// <inheritdoc />
    public override void OnStart(Activity data)
    {
        if (!IsAiFrameworkActivity(data))
            return;

        foreach (var key in BaggageKeys)
        {
            var value = Activity.Current?.GetBaggageItem(key);
            if (value is not null)
                data.SetTag(key, value);
        }
    }

    private static bool IsAiFrameworkActivity(Activity data)
    {
        var sourceName = data.Source.Name;

        if (ExactSources.Contains(sourceName))
            return true;

        return sourceName.Contains("Microsoft.Agents.AI", StringComparison.OrdinalIgnoreCase)
            || sourceName.Contains("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase)
            || sourceName.StartsWith("Microsoft.SemanticKernel", StringComparison.OrdinalIgnoreCase);
    }
}
