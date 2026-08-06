using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Microsoft.Extensions.AI;

namespace Application.Core.CQRS.Agents.RunConversation;

/// <summary>
/// One conversation's durable transcript, bound to the caller it was opened for. Reads the window of
/// prior messages a continuing run replays to the model, and writes each turn back as it completes.
/// </summary>
/// <remarks>
/// <para>
/// This exists so <see cref="RunConversationCommandHandler"/> can hold "the conversation this run is
/// continuing" as one thing rather than as a store, an id and an owner id threaded separately through
/// every call. The owner is captured once, at construction, which is what makes it impossible for a
/// later call in the same run to pass a different one.
/// </para>
/// <para>
/// <strong>It performs no ownership check and must not grow one.</strong> Every method hands the
/// captured caller id to <see cref="IConversationStore"/>, which refuses a conversation belonging to
/// someone else. Re-checking here would be the seventh hand-written copy of a comparison this codebase
/// deliberately moved into the store — see the remarks on <see cref="IConversationStore"/>.
/// </para>
/// </remarks>
internal sealed class DurableTranscript
{
    private readonly IConversationStore _store;
    private readonly string _conversationId;
    private readonly string _ownerId;

    /// <summary>Binds a transcript to the conversation and caller a run is executing for.</summary>
    /// <param name="store">The transcript store. Enforces ownership on every call made here.</param>
    /// <param name="conversationId">The conversation being continued.</param>
    /// <param name="ownerId">The authenticated caller. Must be non-blank.</param>
    public DurableTranscript(IConversationStore store, string conversationId, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        _store = store;
        _conversationId = conversationId;
        _ownerId = ownerId;
    }

    /// <summary>
    /// Reads the conversation's observability session and everything it has spent across every run
    /// before this one.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The conversation's session, or <see cref="Guid.Empty"/> when it has never had one, and its
    /// totals, never <see langword="null"/>. A conversation the store cannot return reads as neither.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Must be called under the turn lease, and is, for the same reason the replay window is.</strong>
    /// A run that queued behind another host's run would otherwise carry totals read before that run
    /// existed, add its own, and write back a sum missing everything the other host spent — silently
    /// deleting a peer's telemetry rather than merely reporting it late.
    /// </para>
    /// <para>
    /// This is a second read of a record the run has already opened, which is the price of taking it at
    /// the right moment rather than the convenient one. It is paid once per run, not once per turn.
    /// </para>
    /// </remarks>
    public async Task<(Guid SessionId, TelemetryAccumulator Totals)> LoadTelemetryAsync(CancellationToken ct)
    {
        var record = await _store.GetAsync(_conversationId, _ownerId, ct);

        return (record?.ObservabilitySessionId ?? Guid.Empty,
                record?.Telemetry ?? TelemetryAccumulator.Zero);
    }

    /// <summary>
    /// Records the conversation's running totals, and the session they belong to, on the conversation
    /// itself.
    /// </summary>
    /// <param name="observabilitySessionId">The conversation's session.</param>
    /// <param name="totals">Cumulative totals across every run so far, not this run's share.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// This is the second of the two places a conversation's telemetry lives, and the one that survives
    /// the observability database being absent or switched off. Writing only the session row would leave
    /// the next <see cref="LoadTelemetryAsync"/> reading zero and starting the overwrite again.
    /// </remarks>
    public Task PersistTelemetryAsync(
        Guid observabilitySessionId, TelemetryAccumulator totals, CancellationToken ct) =>
        _store.UpdateTelemetryAsync(_conversationId, _ownerId, observabilitySessionId, totals, ct);

    /// <summary>
    /// Returns the most recent <paramref name="maxMessages"/> messages, projected onto the framework's
    /// chat shape, ready to seed the run's first turn.
    /// </summary>
    /// <param name="maxMessages">
    /// The window size. Passed through unchanged: the store is explicit that a non-positive value
    /// returns no messages rather than all of them, so "no history" stays a configurable answer here
    /// instead of silently becoming an unbounded prompt.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The window, oldest first. Empty when the conversation holds nothing yet.</returns>
    public async Task<IReadOnlyList<ChatMessage>> LoadHistoryAsync(int maxMessages, CancellationToken ct)
    {
        var messages = await _store.GetHistoryForDispatch(_conversationId, _ownerId, maxMessages, ct);
        if (messages is null || messages.Count == 0)
            return [];

        return ConversationMessageMapping.ToChatMessages(messages);
    }

    /// <summary>
    /// Appends one completed exchange — the question and the answer it produced — to the transcript.
    /// </summary>
    /// <param name="userMessage">The user's message that opened the turn.</param>
    /// <param name="agentResponse">The agent's reply that closed it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// A turn is written as a pair, and only once it has an answer, because this transcript is replayed
    /// to a model rather than read by a person. A question stored without its answer is not an
    /// incomplete record, it is a misleading one: the next run replays a conversation in which the user
    /// asked and was ignored, and the model answers accordingly.
    /// </para>
    /// <para>
    /// Written through the store's batch append, so the pair is one unit: either the whole exchange is
    /// stored or none of it is. Two separate appends could be interrupted between them — most obviously
    /// by the lease being lost, which cancels the token both writes run under — and would leave exactly
    /// the half-turn this method exists to avoid. It is also one file rewrite instead of two on the
    /// file-backed store, whose appends rewrite the entire transcript.
    /// </para>
    /// </remarks>
    public Task AppendTurnAsync(string userMessage, string agentResponse, CancellationToken ct) =>
        _store.AppendMessagesAsync(
            _conversationId,
            _ownerId,
            [
                NewMessage(MessageRole.User, userMessage),
                NewMessage(MessageRole.Assistant, agentResponse),
            ],
            ct);

    /// <remarks>
    /// A fresh id per message rather than a caller-supplied one. The interactive transports preserve the
    /// client's id so an optimistic UI bubble and the stored row agree, and retry-from-message can then
    /// resolve it; a bundle run has no such client and no such bubble, so minting the id here keeps the
    /// store's "ids are unique within a conversation" rule without inventing a protocol to carry one.
    /// </remarks>
    private static ConversationMessage NewMessage(MessageRole role, string content) =>
        new(Guid.NewGuid(), role, content, DateTimeOffset.UtcNow);
}
