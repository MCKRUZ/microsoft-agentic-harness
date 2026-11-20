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
    /// When non-null, the conversation was truncated for this retry or edit: the message count it
    /// kept. Carried for callers that only see the final outcome (logging, telemetry, tests). The
    /// live client notification does NOT read this field: it goes out the moment the turn is
    /// admitted, via <see cref="Interfaces.IConversationOrchestrator.RetryFromMessageAsync"/>'s and
    /// <see cref="Interfaces.IConversationOrchestrator.EditAndResubmitAsync"/>'s
    /// <c>onHistoryTruncated</c> callback, before this turn's deltas (#328). <see langword="null"/>
    /// when the turn was not admitted and the conversation is as it was (#964), except a retry a
    /// verdict removed, which counts what is left (#1036).
    /// </summary>
    public int? HistoryKeepCount { get; init; }

    /// <summary>
    /// The id of the caller's message the conversation does not hold, or <see langword="null"/> when it
    /// does. A send or an edit stores its message only once the turn is admitted (#964), so this names
    /// the message of every turn that was not, and a retried message a verdict removed. The client
    /// shows that message before the turn runs, so the hub names it for the client to drop (#1036).
    /// </summary>
    public Guid? RemovedMessageId { get; init; }

    /// <summary>
    /// True when this turn was declined because the conversation exhausted its lifetime token budget.
    /// A graceful stop, not an error: <see cref="Success"/> stays true and <see cref="Response"/> carries
    /// the explanatory message. The transport may surface this (e.g. disable further input).
    /// </summary>
    public bool BudgetExhausted { get; init; }
}
