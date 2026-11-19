namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Result of a single agent turn dispatched by <see cref="Interfaces.IConversationOrchestrator"/>.
/// The transport layer (hub, REST endpoint) uses this to emit the appropriate client events.
/// </summary>
public sealed record TurnOutcome
{
    public required bool Success { get; init; }
    public string? Response { get; init; }
    public Guid AssistantMessageId { get; init; }
    public int FinalTurnNumber { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// When non-null, indicates the conversation was truncated before dispatch (retry/edit) —
    /// carried here for callers that only see the final outcome (logging, telemetry, tests). The
    /// live client notification does NOT read this field: it already went out before this turn was
    /// even dispatched, via <see cref="Interfaces.IConversationOrchestrator.RetryFromMessageAsync"/>'s
    /// and <see cref="Interfaces.IConversationOrchestrator.EditAndResubmitAsync"/>'s
    /// <c>onHistoryTruncated</c> callback (see their remarks) — waiting for this outcome to emit the
    /// notification is exactly the #328 ordering bug that callback exists to prevent.
    /// It counts what the store holds once the turn ends, so a refused retry whose message was then
    /// removed (<see cref="RemovedMessageId"/>) reports one fewer than the pre-dispatch notice did (#1036).
    /// </summary>
    public int? HistoryKeepCount { get; init; }

    /// <summary>
    /// The id of the caller's message the store dropped because the turn was rejected before the
    /// model saw it, or <see langword="null"/> when the store still holds it. The client shows that
    /// message before the turn runs, so the hub names it on the <c>Error</c> event for the client to
    /// drop (#1036).
    /// </summary>
    public Guid? RemovedMessageId { get; init; }

    /// <summary>
    /// True when this turn was declined because the conversation exhausted its lifetime token budget.
    /// A graceful stop, not an error: <see cref="Success"/> stays true and <see cref="Response"/> carries
    /// the explanatory message. The transport may surface this (e.g. disable further input).
    /// </summary>
    public bool BudgetExhausted { get; init; }
}
