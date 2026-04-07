using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Instruments;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;
using OpenTelemetry;

namespace Application.AI.Common.OpenTelemetry.Processors;

/// <summary>
/// Enriches Microsoft Agent Framework tool execution spans by normalizing telemetry
/// attributes for consistent querying. Copies <c>gen_ai.tool.call.result</c> to
/// <c>gen_ai.event.content</c> for <c>execute_tool</c> operations.
/// </summary>
/// <remarks>
/// Only processes activities from the Agent Framework source. The source name is
/// centralized in <see cref="AiSourceNames"/> for maintainability — the
/// <c>Experimental</c> prefix will change when the SDK reaches GA.
/// </remarks>
public sealed class AgentFrameworkSpanProcessor : BaseProcessor<Activity>
{
    private const string EventContentTag = "gen_ai.event.content";
    // Centralized in AiSourceNames — single place to update at SDK GA
    private static readonly string AgentFrameworkSource = AiSourceNames.AgentFrameworkExact;

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (!string.Equals(data.Source.Name, AgentFrameworkSource, StringComparison.Ordinal))
            return;

        var opName = data.GetTagItem(ToolConventions.GenAiOperationName) as string;
        if (!string.Equals(opName, ToolConventions.ExecuteToolOperation, StringComparison.Ordinal))
            return;

        var toolResult = data.GetTagItem(ToolConventions.ToolCallResult) as string;
        if (toolResult is not null)
        {
            var truncated = toolResult.Length > ToolConventions.MaxResultLength
                ? string.Concat(toolResult.AsSpan(0, ToolConventions.MaxResultLength), "...[truncated]")
                : toolResult;
            data.SetTag(EventContentTag, truncated);
        }
    }
}
