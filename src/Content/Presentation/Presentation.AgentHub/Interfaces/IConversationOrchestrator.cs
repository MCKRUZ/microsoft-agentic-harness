using Application.AI.Common.Models.Conversations;
using Presentation.AgentHub.DTOs;

namespace Presentation.AgentHub.Interfaces;

/// <summary>
/// Owns conversation lifecycle, turn orchestration, ownership validation, and session
/// management. Transport layers (SignalR hub, REST, gRPC) delegate all business logic
/// here and handle only protocol-specific concerns (group management, event broadcasting).
/// </summary>
public interface IConversationOrchestrator
{
    /// <summary>
    /// Joins or creates a conversation. Validates ownership when <paramref name="conversationId"/>
    /// references an existing record.
    /// </summary>
    /// <returns>The conversation record and its capped message history.</returns>
    Task<(ConversationRecord Record, IReadOnlyList<ConversationMessage> History)> StartConversationAsync(
        string sessionKey, string agentName, string? conversationId, string callerId, CancellationToken ct);

    /// <summary>
    /// Replaces per-conversation agent settings. Validates ownership before writing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Conversation not found.</exception>
    /// <exception cref="UnauthorizedAccessException">Caller does not own the conversation.</exception>
    Task SetSettingsAsync(
        string conversationId, ConversationSettings settings, string callerId, CancellationToken ct);

    /// <summary>
    /// Dispatches an agent turn for a user message, storing the message only once the turn is admitted:
    /// after every request check has passed (#964). A turn refused or failed before that stores nothing,
    /// and the outcome names the message in <see cref="TurnOutcome.RemovedMessageId"/>. Streams response
    /// chunks via <paramref name="onChunk"/> if provided. Acquires the per-conversation lock.
    /// </summary>
    Task<TurnOutcome> SendMessageAsync(
        string sessionKey, string conversationId, Guid userMessageId, string message, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct);

    /// <summary>
    /// Re-dispatches the user message preceding the specified assistant message. The old answer is
    /// removed only once the turn is admitted (#964): a retry refused by an unavailable screening
    /// service keeps it, while one refused by a screening verdict also removes the user message.
    /// </summary>
    /// <param name="onHistoryTruncated">
    /// Invoked once with what the truncation removed, the first removed message's id and the surviving
    /// message count (<see cref="HistoryTruncation"/>), when the truncation commits at admission, BEFORE
    /// the model runs — so a caller streaming <paramref name="onChunk"/> can tell a client to drop its
    /// stale local tail before any of this turn's own deltas arrive. Emitting the truncation signal
    /// only after the turn completes (via the returned <see cref="TurnOutcome.HistoryKeepCount"/>)
    /// would let a client append this turn's streamed response onto its still-untruncated message
    /// list first — the ordering bug this parameter exists to prevent (#328). A failure calling this
    /// delegate, a cancellation included, is caught and logged, never propagated: the truncation it
    /// announces has already committed durably.
    /// </param>
    Task<TurnOutcome> RetryFromMessageAsync(
        string sessionKey, string conversationId, Guid assistantMessageId, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
        Func<HistoryTruncation, CancellationToken, Task>? onHistoryTruncated = null);

    /// <summary>
    /// Replaces the specified user message, and everything after it, with the edited content and
    /// re-dispatches. The replacement happens only once the turn is admitted (#962, #964): a refused
    /// edit leaves the conversation as it was.
    /// </summary>
    /// <param name="onHistoryTruncated">
    /// Invoked once with what the truncation removed (<see cref="HistoryTruncation"/>), at admission after the
    /// edited message is durably
    /// appended, before the model runs — see
    /// <see cref="RetryFromMessageAsync"/>'s remarks on this parameter for why the ordering
    /// relative to dispatch matters, and on why a delegate failure is caught rather than propagated.
    /// The append happens first here specifically (unlike <see cref="RetryFromMessageAsync"/>, which
    /// has no equivalent append) so a client acting on this notice — typically by optimistically
    /// re-inserting the edited message — never does so before the edit is actually persisted.
    /// </param>
    Task<TurnOutcome> EditAndResubmitAsync(
        string sessionKey, string conversationId, Guid userMessageId, Guid newUserMessageId,
        string newContent, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
        Func<HistoryTruncation, CancellationToken, Task>? onHistoryTruncated = null);

    /// <summary>
    /// Rebinds the conversation to a different agent. A no-op reassignment (the requested name
    /// already matches the conversation's current agent) is a read-only check. See the
    /// implementation's remarks for why the write and the resulting cache eviction are synchronized
    /// under the same per-conversation turn lease that guards ordinary message dispatch — that
    /// synchronization is load-bearing, not incidental.
    /// </summary>
    /// <returns>The updated record, or <see langword="null"/> if the conversation does not exist.</returns>
    /// <exception cref="UnauthorizedAccessException">Caller does not own the conversation.</exception>
    Task<ConversationRecord?> ReassignAgentAsync(
        string conversationId, string callerId, string agentName, CancellationToken ct);

    /// <summary>
    /// Validates that <paramref name="callerId"/> owns the conversation. Throws
    /// <see cref="InvalidOperationException"/> if not found, <see cref="UnauthorizedAccessException"/>
    /// if owned by a different user.
    /// </summary>
    Task ValidateAccessAsync(string conversationId, string callerId, CancellationToken ct);

    /// <summary>
    /// Cleans up session state for a disconnected connection: untracks, records metrics,
    /// and ends the observability session.
    /// </summary>
    Task HandleDisconnectAsync(string sessionKey, Exception? exception, CancellationToken ct);
}
