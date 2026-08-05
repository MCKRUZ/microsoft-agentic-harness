using Application.AI.Common.Models.Conversations;

namespace Application.AI.Common.Interfaces.AI;

/// <summary>
/// Persistent store for conversation records. Thread-safe for concurrent access.
/// <strong>Enforces user-ownership isolation itself</strong> — every operation that names a single
/// conversation takes the caller's identity and refuses to serve a record owned by anyone else.
/// </summary>
/// <remarks>
/// <para>
/// Ownership lives here rather than in each caller deliberately. It previously did not, and the
/// comparison <c>record.UserId != callerId</c> ended up hand-written in six places across four
/// files with three different failure shapes; a seventh entry point (Execution API bundle runs) was
/// about to be added. A check every caller must remember is a check that is eventually forgotten,
/// and the cost of forgetting it once is one user reading another user's transcript.
/// </para>
/// <para>
/// <strong>Fail closed.</strong> A blank <c>callerId</c> is an <see cref="ArgumentException"/>, never
/// a wildcard. This is not defensive noise: this codebase has three recorded incidents of an absent
/// identity being read as "global", so the absence of an identity must be an error at the boundary
/// rather than a value that flows onward and widens access.
/// </para>
/// <para>
/// <strong>A refusal throws rather than returning a <c>Result</c>,</strong> which is a deliberate
/// departure from the house rule that expected failures — auth among them — are returned. Two
/// reasons: this is a persistence port, not a CQRS command, and the callers it replaces already
/// threw <see cref="UnauthorizedAccessException"/>, so returning instead would have left every
/// transport translating a new shape while the old one still arrived from elsewhere. The cost is
/// that a caller can forget to handle it; the mitigation is that forgetting fails closed, as an
/// unhandled refusal, rather than open.
/// </para>
/// <para>
/// The two failure modes stay distinguishable, because the HTTP surface distinguishes them: a
/// conversation that does not exist reads as <c>null</c> (or a no-op), whereas one that exists but
/// belongs to another user throws <see cref="UnauthorizedAccessException"/>. Callers that must not
/// disclose existence should map both onto one response themselves.
/// </para>
/// <para>
/// Conversation ids are caller-supplied, and an implementation may impose its own constraints on
/// them — the file-backed store makes each id a file name and rejects any that escapes its base
/// directory, whereas the SQLite store accepts whatever it is given (its declared column length is
/// not enforced by SQLite). Callers should treat an id as an opaque token they generated, not as
/// arbitrary text, and must not rely on a particular rejection.
/// </para>
/// </remarks>
public interface IConversationStore
{
    /// <summary>Returns the conversation with the given ID, or <c>null</c> if it does not exist.</summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    Task<ConversationRecord?> GetAsync(string conversationId, string callerId, CancellationToken ct = default);

    /// <summary>
    /// Returns all conversations owned by <paramref name="userId"/>.
    /// O(n) in the number of stored conversations — acceptable for POC scale.
    /// </summary>
    /// <param name="userId">The caller, which is also the owner filter. Must be non-blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// This is the one operation whose identity argument was always a scope rather than a check —
    /// it lists what belongs to that user and nothing else. It is still rejected when blank, because
    /// "list the conversations belonging to nobody in particular" has no safe reading.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is blank.</exception>
    Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new conversation owned by <paramref name="userId"/>. If
    /// <paramref name="conversationId"/> is supplied and non-empty the record uses that value as its
    /// ID (caller-generated GUID for idempotent reconnect); otherwise a new GUID is generated.
    /// </summary>
    /// <param name="agentName">The agent the conversation is bound to.</param>
    /// <param name="userId">The owner, which is also the caller. Must be non-blank.</param>
    /// <param name="conversationId">An optional caller-supplied id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Supplying the id of an <em>existing</em> conversation <strong>replaces</strong> it, transcript
    /// included — so this is the one create path that can destroy data, and it refuses when the
    /// existing record belongs to someone else. Without that refusal a caller could name any id,
    /// delete a stranger's transcript, and take the id over. That was unreachable only because every
    /// caller happened to check ownership first, which is exactly the arrangement this interface
    /// stopped relying on.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// A conversation with this id already exists and belongs to another user. Nothing is written.
    /// </exception>
    Task<ConversationRecord> CreateAsync(string agentName, string userId, string? conversationId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the conversation with <paramref name="conversationId"/>, creating an empty one owned by
    /// <paramref name="userId"/> if it does not exist. Never replaces an existing transcript.
    /// </summary>
    /// <param name="agentName">The agent to bind a newly created conversation to. Ignored if one exists.</param>
    /// <param name="userId">The caller, and the owner of a conversation created here. Must be non-blank.</param>
    /// <param name="conversationId">The conversation to open. Must be non-blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="CreateAsync"/> <em>replaces</em>, which makes the obvious
    /// composition — read, and create when the read came back empty — a transcript-destroying race
    /// rather than merely a redundant one. Two runs opening the same new conversation can both see it
    /// absent; if the loser's create lands after the winner has already appended a turn, the winner's
    /// messages are deleted by the cascade and nothing reports an error. The window is one store
    /// round-trip wide, which is small enough to survive review and far too large to run a transcript
    /// through.
    /// </para>
    /// <para>
    /// Implementations must make the create atomic against a concurrent create of the same id, and must
    /// resolve a lost race by returning the winner's record — not by overwriting it. The turn lease
    /// cannot stand in for this: a lease claims a conversation that already exists, so there is nothing
    /// for it to hold while one is being created.
    /// </para>
    /// <para>
    /// Ownership is enforced on both outcomes, so losing the create race to another user is refused
    /// exactly as reading their conversation would be.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="userId"/> or <paramref name="conversationId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation exists and belongs to another user.</exception>
    Task<ConversationRecord> GetOrCreateAsync(
        string agentName,
        string userId,
        string conversationId,
        CancellationToken ct = default);

    /// <summary>Appends <paramref name="message"/> to an existing conversation record.</summary>
    /// <param name="conversationId">The conversation to append to.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="message">
    /// The message. Its id must not already be present in this conversation — ids are client-supplied,
    /// and a replayed submit would otherwise leave two rows sharing one id, which makes a later
    /// truncation's cut point arbitrary. The same id in a <em>different</em> conversation is fine.
    /// An empty id is assigned one.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    /// <exception cref="InvalidOperationException">
    /// The conversation does not exist, or it already holds a message with this id. Nothing is
    /// written in either case.
    /// </exception>
    Task AppendMessageAsync(string conversationId, string callerId, ConversationMessage message, CancellationToken ct = default);

    /// <summary>
    /// Appends several messages as one unit, in the order given. Either all of them are stored or none
    /// is.
    /// </summary>
    /// <param name="conversationId">The conversation to append to.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="messages">
    /// The messages, oldest first. An empty list is a no-op. The same id rules as
    /// <see cref="AppendMessageAsync"/> apply, and they apply <em>within</em> this batch too: two
    /// messages here sharing one id is the same defect as one colliding with a stored message.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// This exists for the caller that has a complete exchange to store — a question and the answer it
    /// produced — and it is not merely a convenience over calling <see cref="AppendMessageAsync"/>
    /// twice. Two calls are two writes, and both of the shipped stores pay a per-call cost that the
    /// batch pays once: the file-backed store rewrites the entire transcript on every append, so
    /// storing a turn as two appends rewrites a linearly growing file twice per turn — quadratic bytes
    /// over a session, which is precisely the growth the durable-conversation work exists to remove
    /// from the token bill.
    /// </para>
    /// <para>
    /// Atomicity is the other half. A turn split across two writes can be interrupted between them,
    /// leaving a question with no answer — and this transcript is replayed to a model, so a half-turn
    /// is not an incomplete record but a misleading one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    /// <exception cref="InvalidOperationException">
    /// The conversation does not exist, or a message id is already present. Nothing is written.
    /// </exception>
    Task AppendMessagesAsync(
        string conversationId,
        string callerId,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken ct = default);

    /// <summary>Permanently deletes a conversation record.</summary>
    /// <param name="conversationId">The conversation to delete.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if a conversation was deleted; <c>false</c> if none existed.</returns>
    /// <remarks>
    /// The return value exists so a caller can still answer "not found" without reading the record
    /// first. Ownership moving into the store would otherwise have forced every delete endpoint into
    /// a read-then-delete pair purely to tell absence from success — reintroducing, for presentation
    /// reasons, exactly the two-step shape this interface set out to remove.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    Task<bool> DeleteAsync(string conversationId, string callerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the last <paramref name="maxMessages"/> messages from the conversation,
    /// or <c>null</c> if the conversation does not exist.
    /// Called by the hub before dispatching to the agent to prevent unbounded token growth.
    /// </summary>
    /// <param name="conversationId">The conversation to read.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="maxMessages">
    /// The most messages to return. Zero or negative returns none — never everything. Stated here
    /// because the natural SQL translation of a window is <c>LIMIT</c>, and SQLite reads a negative
    /// <c>LIMIT</c> as no limit at all: an implementation that passes the value straight through
    /// answers a request for no history with the whole transcript, unbounded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
        string conversationId,
        string callerId,
        int maxMessages,
        CancellationToken ct = default);

    /// <summary>
    /// Truncates the conversation so that the message with <paramref name="messageId"/> and
    /// every message after it are removed. No-op if the conversation or message does not exist.
    /// Used by retry/edit flows to drop the superseded tail before re-dispatching to the agent.
    /// </summary>
    /// <param name="conversationId">The conversation to truncate.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="messageId">The first message to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The truncated conversation record, or <c>null</c> if it did not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    Task<ConversationRecord?> TruncateFromMessageAsync(
        string conversationId,
        string callerId,
        Guid messageId,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces the <see cref="ConversationRecord.Settings"/> for the specified conversation.
    /// Returns the updated record, or <c>null</c> if the conversation does not exist.
    /// </summary>
    /// <param name="conversationId">The conversation to update.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    Task<ConversationRecord?> UpdateSettingsAsync(
        string conversationId,
        string callerId,
        ConversationSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the observability session ID and telemetry accumulator for the specified
    /// conversation. Called by the AG-UI handler after each turn to carry session state
    /// across stateless HTTP requests.
    /// Returns the updated record, or <c>null</c> if the conversation does not exist.
    /// </summary>
    /// <param name="conversationId">The conversation to update.</param>
    /// <param name="callerId">The authenticated caller. Must be non-blank.</param>
    /// <param name="observabilitySessionId">The observability session to carry forward.</param>
    /// <param name="telemetry">The accumulated per-conversation telemetry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="callerId"/> is blank.</exception>
    /// <exception cref="UnauthorizedAccessException">The conversation belongs to another user.</exception>
    Task<ConversationRecord?> UpdateTelemetryAsync(
        string conversationId,
        string callerId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default);
}
