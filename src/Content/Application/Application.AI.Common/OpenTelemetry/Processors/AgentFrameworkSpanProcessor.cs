using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Instruments;
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
    private const int MaxToolResultLength = 4096;
    private const string ExecuteToolOperation = "execute_tool";
    private const string ToolCallResultTag = "gen_ai.tool.call.result";
    private const string EventContentTag = "gen_ai.event.content";
    private const string GenAIOperationNameKey = "gen_ai.operation.name";
    // Centralized in AiSourceNames — single place to update at SDK GA
    private static readonly string AgentFrameworkSource = AiSourceNames.AgentFrameworkExact;

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (!string.Equals(data.Source.Name, AgentFrameworkSource, StringComparison.Ordinal))
            return;

        var opName = data.GetTagItem(GenAIOperationNameKey) as string;
        if (!string.Equals(opName, ExecuteToolOperation, StringComparison.Ordinal))
            return;

        var toolResult = data.GetTagItem(ToolCallResultTag) as string;
        if (toolResult is not null)
        {
            var truncated = toolResult.Length > MaxToolResultLength
                ? string.Concat(toolResult.AsSpan(0, MaxToolResultLength), "...[truncated]")
                : toolResult;
            data.SetTag(EventContentTag, truncated);
        }
    }
}
