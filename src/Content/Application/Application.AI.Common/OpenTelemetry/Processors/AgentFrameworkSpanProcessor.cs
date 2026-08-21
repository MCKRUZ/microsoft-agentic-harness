using System.Diagnostics;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.OpenTelemetry.Instruments;
using Domain.AI.Telemetry.Conventions;
using Domain.AI.Telemetry.Redaction;
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

    private readonly IContentRedactionFilter _filter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFrameworkSpanProcessor"/> class.
    /// </summary>
    /// <param name="filter">Redacts the tool result before it is copied onto the span.</param>
    public AgentFrameworkSpanProcessor(IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter;
    }

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
            // Redact before truncating: truncating first could split a secret in half and
            // leave the surviving fragment unredacted.
            var redacted = _filter.Redact(toolResult, RedactionCategories.All);
            var truncated = redacted.Length > ToolConventions.MaxResultLength
                ? string.Concat(redacted.AsSpan(0, ToolConventions.MaxResultLength), "...[truncated]")
                : redacted;
            data.SetTag(EventContentTag, truncated);
        }
    }
}
