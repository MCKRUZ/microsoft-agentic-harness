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
    /// Appends a user message and dispatches an agent turn. Streams response chunks via
    /// <paramref name="onChunk"/> if provided. Acquires the per-conversation lock.
    /// </summary>
    Task<TurnOutcome> SendMessageAsync(
        string sessionKey, string conversationId, Guid userMessageId, string message, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct);

    /// <summary>
    /// Truncates from the specified assistant message and re-dispatches the preceding user message.
    /// </summary>
    /// <param name="onHistoryTruncated">
    /// Invoked once with the surviving message count immediately after truncation, BEFORE
    /// dispatching the retried turn — so a caller streaming <paramref name="onChunk"/> can tell a
    /// client to drop its stale local tail before any of this turn's own deltas arrive. Emitting
    /// the truncation signal only after the turn completes (via the returned
    /// <see cref="TurnOutcome.HistoryKeepCount"/>) would let a client append this turn's streamed
    /// response onto its still-untruncated message list first — the ordering bug this parameter
    /// exists to prevent (#328). A failure calling this delegate is caught and logged by the
    /// implementation, never propagated — the truncation it announces has already committed
    /// durably, so a failed notification must not abort the turn.
    /// </param>
    Task<TurnOutcome> RetryFromMessageAsync(
        string sessionKey, string conversationId, Guid assistantMessageId, string callerId,
        Func<string, CancellationToken, Task>? onChunk, CancellationToken ct,
        Func<int, CancellationToken, Task>? onHistoryTruncated = null);

    /// <summary>
    /// Truncates from the specified user message, appends edited content, and re-dispatches.
    /// </summary>
    /// <param name="onHistoryTruncated">
    /// Invoked once with the surviving message count, after the edited message is durably appended
    /// and immediately before dispatching the resubmitted turn — see
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
        Func<int, CancellationToken, Task>? onHistoryTruncated = null);

    /// <summary>
    /// Rebinds the conversation to a different agent, synchronized under the same per-conversation
    /// turn lease that guards ordinary message dispatch. That synchronization is load-bearing, not
    /// incidental: writing the new agent name and evicting the cached agent are two separate steps,
    /// and without a shared lock a turn racing this call could read the newly-written agent name from
    /// the database yet still be served the stale cached agent, because <c>IAgentConversationCache</c>
    /// returns a cache hit unconditionally and eviction has not necessarily run yet. A no-op
    /// reassignment (the requested name already matches the conversation's current agent) writes
    /// nothing and evicts nothing, so it does not discard a live, correctly-configured cached agent
    /// for no behavioral reason.
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
