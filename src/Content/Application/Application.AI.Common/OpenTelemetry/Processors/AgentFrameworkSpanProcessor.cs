using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.OpenTelemetry.Instruments;
using Application.AI.Common.Services.Governance;
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
    private const string TruncationMarker = "...[truncated]";
    // Centralized in AiSourceNames — single place to update at SDK GA
    private static readonly string AgentFrameworkSource = AiSourceNames.AgentFrameworkExact;

    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _filter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentFrameworkSpanProcessor"/> class.
    /// </summary>
    /// <param name="sanitizer">
    /// Sanitizes the tool result before it is redacted (#470) — strips injection payloads and
    /// canonicalizes invisible/zero-width characters, so a secret split by them can't dodge the
    /// redaction filter's anchored patterns the way it could when this span only redacted.
    /// </param>
    /// <param name="filter">Redacts the tool result before it is copied onto the span.</param>
    public AgentFrameworkSpanProcessor(ICompositeResponseSanitizer sanitizer, IContentRedactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(filter);
        _sanitizer = sanitizer;
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
            // Sanitize before redact (#470), truncate last: truncating first could split a secret in
            // half and leave the surviving fragment unredacted.
            var treated = SanitizeThenRedact.Apply(toolResult, _sanitizer, _filter, RedactionCategories.All);
            var (truncated, _) = BoundedText.Cap(treated, ToolConventions.MaxResultLength, TruncationMarker);
            data.SetTag(EventContentTag, truncated);
        }
    }
}
