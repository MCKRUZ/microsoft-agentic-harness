namespace Presentation.AgentHub.Telemetry;

/// <summary>
/// Immutable snapshot of an OpenTelemetry span, serialized over SignalR to connected WebUI clients.
/// Field names map directly to the TypeScript <c>SpanData</c> interface in the WebUI project.
/// </summary>
/// <param name="Name">Display name of the span (Activity.DisplayName).</param>
/// <param name="TraceId">Hex-encoded trace ID.</param>
/// <param name="SpanId">Hex-encoded span ID.</param>
/// <param name="ParentSpanId">Hex-encoded parent span ID, or <see langword="null"/> for root spans.</param>
/// <param name="ConversationId">Value of the <c>agent.conversation_id</c> activity tag; <see langword="null"/> for non-agent spans.</param>
/// <param name="StartTime">UTC start time of the span.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Status">Normalized status string: <c>"unset"</c>, <c>"ok"</c>, or <c>"error"</c>.</param>
/// <param name="StatusDescription">Optional status description set by the instrumentation library.</param>
/// <param name="Kind">Normalized kind string: <c>"internal"</c>, <c>"client"</c>, or <c>"server"</c>.</param>
/// <param name="SourceName">Name of the <see cref="System.Diagnostics.ActivitySource"/> that produced the span.</param>
/// <param name="Tags">All string-valued tags attached to the span.</param>
public record SpanData(
    string Name,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string? ConversationId,
    DateTimeOffset StartTime,
    double DurationMs,
    string Status,
    string? StatusDescription,
    string Kind,
    string SourceName,
    IReadOnlyDictionary<string, string> Tags
);
