using Application.AI.Common.Models.Conversations;

namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Persistent store for conversation records. Thread-safe for concurrent access.
/// Implementations must enforce user-ownership isolation — callers are responsible
/// for checking <see cref="ConversationRecord.UserId"/> against the authenticated user
/// before returning records to clients.
/// </summary>
/// <remarks>
/// <para>
/// Conversation ids are caller-supplied, and an implementation may impose its own constraints on
/// them — the file-backed store makes each id a file name and rejects any that escapes its base
/// directory, whereas the SQLite store accepts whatever it is given (its declared column length is
/// not enforced by SQLite). Callers should treat an id as an opaque token they generated, not as
/// arbitrary text, and must not rely on a particular rejection.
/// </para>
/// <para>
/// <c>CreateAsync</c> with an id that already exists <strong>replaces</strong> that conversation,
/// transcript included. Every caller in the harness reaches it only after a read returned nothing,
/// so this is defined behaviour rather than a path anything takes.
/// </para>
/// </remarks>
public interface IConversationStore
{
    /// <summary>Returns the conversation with the given ID, or <c>null</c> if it does not exist.</summary>
    Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns all conversations owned by <paramref name="userId"/>.
    /// O(n) in the number of stored conversations — acceptable for POC scale.
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new conversation. If <paramref name="conversationId"/> is supplied and non-empty
    /// the record uses that value as its ID (caller-generated GUID for idempotent reconnect);
    /// otherwise a new GUID is generated.
    /// </summary>
    Task<ConversationRecord> CreateAsync(string agentName, string userId, string? conversationId = null, CancellationToken ct = default);

    /// <summary>Appends <paramref name="message"/> to an existing conversation record.</summary>
    /// <param name="conversationId">The conversation to append to.</param>
    /// <param name="message">
    /// The message. Its id must not already be present in this conversation — ids are client-supplied,
    /// and a replayed submit would otherwise leave two rows sharing one id, which makes a later
    /// truncation's cut point arbitrary. The same id in a <em>different</em> conversation is fine.
    /// An empty id is assigned one.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// The conversation does not exist, or it already holds a message with this id. Nothing is
    /// written in either case.
    /// </exception>
    Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default);

    /// <summary>Permanently deletes a conversation record.</summary>
    Task DeleteAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the last <paramref name="maxMessages"/> messages from the conversation,
    /// or <c>null</c> if the conversation does not exist.
    /// Called by the hub before dispatching to the agent to prevent unbounded token growth.
    /// </summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="maxMessages">
    /// The most messages to return. Zero or negative returns none — never everything. Stated here
    /// because the natural SQL translation of a window is <c>LIMIT</c>, and SQLite reads a negative
    /// <c>LIMIT</c> as no limit at all: an implementation that passes the value straight through
    /// answers a request for no history with the whole transcript, unbounded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
        string conversationId,
        int maxMessages,
        CancellationToken ct = default);

    /// <summary>
    /// Truncates the conversation so that the message with <paramref name="messageId"/> and
    /// every message after it are removed. No-op if the conversation or message does not exist.
    /// Used by retry/edit flows to drop the superseded tail before re-dispatching to the agent.
    /// </summary>
    /// <returns>The truncated conversation record, or <c>null</c> if it did not exist.</returns>
    Task<ConversationRecord?> TruncateFromMessageAsync(
        string conversationId,
        Guid messageId,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces the <see cref="ConversationRecord.Settings"/> for the specified conversation.
    /// Returns the updated record, or <c>null</c> if the conversation does not exist.
    /// Caller is responsible for ownership validation before invocation.
    /// </summary>
    Task<ConversationRecord?> UpdateSettingsAsync(
        string conversationId,
        ConversationSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the observability session ID and telemetry accumulator for the specified
    /// conversation. Called by the AG-UI handler after each turn to carry session state
    /// across stateless HTTP requests.
    /// Returns the updated record, or <c>null</c> if the conversation does not exist.
    /// </summary>
    Task<ConversationRecord?> UpdateTelemetryAsync(
        string conversationId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default);
}
