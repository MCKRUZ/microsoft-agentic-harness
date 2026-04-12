using System.Text.Json;

namespace Application.AI.Common.Interfaces.Memory;

/// <summary>
/// Immutable record capturing a single agent decision event for the history log.
/// </summary>
/// <remarks>
/// Events are written to <c>decisions.jsonl</c> in the trace run directory. The
/// <see cref="Sequence"/> is assigned by the store and is monotonically increasing
/// per store instance. All properties are init-only.
/// </remarks>
public record AgentDecisionEvent
{
    /// <summary>Monotonically increasing sequence number assigned by the store.</summary>
    public long Sequence { get; init; }

    /// <summary>UTC timestamp of the event.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Categorizes the event: <c>"tool_call"</c>, <c>"tool_result"</c>,
    /// <c>"decision"</c>, or <c>"observation"</c>.
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>Correlation identifier linking this event to an execution run.</summary>
    public required string ExecutionRunId { get; init; }

    /// <summary>The conversation turn during which this event occurred.</summary>
    public required string TurnId { get; init; }

    /// <summary>Tool name for <c>tool_call</c> and <c>tool_result</c> events; null otherwise.</summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Bucketed outcome: <c>"success"</c>, <c>"partial"</c>, <c>"error"</c>,
    /// <c>"timeout"</c>, or <c>"blocked"</c>. Null for non-result events.
    /// </summary>
    public string? ResultCategory { get; init; }

    /// <summary>Optional structured event payload.</summary>
    public JsonElement? Payload { get; init; }
}

/// <summary>
/// Filter parameters for querying the agent decision log.
/// </summary>
public record DecisionLogQuery
{
    /// <summary>Required. Only events for this execution run are returned.</summary>
    public required string ExecutionRunId { get; init; }

    /// <summary>Optional. Filter by conversation turn.</summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// Optional. Filter by event type: <c>"tool_call"</c>, <c>"tool_result"</c>,
    /// <c>"decision"</c>, or <c>"observation"</c>.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>Optional. Filter by tool name.</summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Sequence checkpoint. Only events with <c>Sequence &gt; Since</c> are returned.
    /// Default is 0 (return all events).
    /// </summary>
    public long Since { get; init; } = 0;

    /// <summary>Maximum number of events to return. Default is 100.</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Append-only, queryable log of agent decision events for a single execution run.
/// Written to <c>decisions.jsonl</c> in the trace run directory.
/// </summary>
/// <remarks>
/// <para>Thread-safe for concurrent appends.</para>
/// <para>One store instance corresponds to one <see cref="Traces.ITraceWriter"/> instance.</para>
/// </remarks>
public interface IAgentHistoryStore
{
    /// <summary>
    /// Appends a decision event to the log. Thread-safe. The <see cref="AgentDecisionEvent.Sequence"/>
    /// is assigned by the store and monotonically increases across concurrent callers.
    /// </summary>
    /// <param name="evt">The event to append. <see cref="AgentDecisionEvent.Sequence"/> is ignored
    /// and overwritten by the store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(AgentDecisionEvent evt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams matching events from the log. Filters applied in order: <c>ExecutionRunId</c>,
    /// <c>EventType</c>, <c>ToolName</c>, <c>TurnId</c>, <c>Since</c> (sequence checkpoint).
    /// Bounded by <c>Limit</c>.
    /// </summary>
    /// <remarks>
    /// Returns an empty sequence if <c>decisions.jsonl</c> does not exist. Never throws for
    /// missing files — only for I/O errors.
    /// </remarks>
    IAsyncEnumerable<AgentDecisionEvent> QueryAsync(
        DecisionLogQuery query,
        CancellationToken cancellationToken = default);
}
