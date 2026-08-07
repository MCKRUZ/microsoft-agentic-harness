using Domain.AI.Context;
using Domain.AI.Observability.Models;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Persists and retrieves structured observability data (sessions, messages, tool executions,
/// safety events, and audit entries) to/from a durable store for historical analytics
/// and Grafana dashboard queries.
/// </summary>
public interface IObservabilityStore
{
    // ── Sessions ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new session record when a conversation begins.
    /// Returns the database-assigned session ID for correlating child records.
    /// </summary>
    Task<Guid> StartSessionAsync(
        string conversationId,
        string agentName,
        string? model,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a session as finished and records its final duration.
    /// </summary>
    /// <param name="sessionId">The session to close. <see cref="Guid.Empty"/> is a no-op.</param>
    /// <param name="status">
    /// How it finished. Typed rather than free text: the column is guarded by a CHECK constraint, and
    /// two callers used to pass words outside it, whereupon the write was rejected and swallowed and
    /// the session never ended at all. See <see cref="SessionStatus"/>.
    /// </param>
    /// <param name="errorMessage">Why it finished this way, when that is worth recording.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task EndSessionAsync(
        Guid sessionId,
        SessionStatus status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reopens a session that was previously ended, so a conversation that continues is no longer
    /// described by a row saying it finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session row is keyed one per conversation, but the decision to end one is taken per
    /// connection — the hub ends a session when the connection holding it disconnects or switches
    /// away, which is the only moment it can, yet says nothing about whether the conversation is over.
    /// Coming back to that conversation continues it, and every turn after that was being written into
    /// a row marked finished, carrying a past end time and a duration that kept growing (issue #289).
    /// </para>
    /// <para>
    /// This is the counterpart of adoption rather than a transport's rule: whoever adopts an existing
    /// session is asserting the conversation is live, so the row must say so. Implementations must be
    /// idempotent — a session that is already open is left exactly as it is, including its start time.
    /// </para>
    /// </remarks>
    /// <param name="sessionId">The session to reopen. <see cref="Guid.Empty"/> is a no-op.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task ResumeSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the running aggregate metrics for a session (called after each turn).
    /// </summary>
    Task UpdateSessionMetricsAsync(
        Guid sessionId,
        int turnCount,
        int toolCallCount,
        int subagentCount,
        int totalInputTokens,
        int totalOutputTokens,
        int totalCacheRead,
        int totalCacheWrite,
        decimal totalCostUsd,
        decimal cacheHitRate,
        string? model = null,
        CancellationToken cancellationToken = default);

    // ── Messages ─────────────────────────────────────────────────────────

    /// <summary>
    /// Records a single conversation turn (user message, assistant response, or tool result).
    /// Returns the message ID for correlating tool executions.
    /// </summary>
    /// <param name="contentFull">
    /// Full message body. When non-null, persisted alongside the truncated
    /// <paramref name="contentPreview"/> so the file-body deep-link endpoint
    /// (<c>GET /api/sessions/{id}/messages/{messageId}</c>) can serve the
    /// original content. Pass <c>null</c> for tool-result rows that have no
    /// distinct full body or when storage is disabled.
    /// </param>
    Task<Guid> RecordMessageAsync(
        Guid sessionId,
        int turnIndex,
        string role,
        string? source,
        string? contentPreview,
        string? model,
        int inputTokens,
        int outputTokens,
        int cacheRead,
        int cacheWrite,
        decimal costUsd,
        decimal cacheHitPct,
        string[]? toolNames = null,
        string? contentFull = null,
        CancellationToken cancellationToken = default);

    // ── Tool Executions ──────────────────────────────────────────────────

    /// <summary>
    /// Records a single tool invocation with its outcome and performance data.
    /// </summary>
    /// <param name="callId">LLM-supplied call id (FunctionCallContent.CallId).</param>
    /// <param name="args">JSON-serialized arguments captured from the LLM's tool-call request.</param>
    /// <param name="stdout">Result payload returned to the LLM (FunctionResultContent.Result).</param>
    Task RecordToolExecutionAsync(
        Guid sessionId,
        Guid? messageId,
        string toolName,
        string toolSource,
        int durationMs,
        string status,
        string? errorType = null,
        int? resultSize = null,
        string? callId = null,
        string? args = null,
        string? stdout = null,
        CancellationToken cancellationToken = default);

    // ── Safety Events ────────────────────────────────────────────────────

    /// <summary>
    /// Records a content safety evaluation result (pass, block, or redact).
    /// </summary>
    Task RecordSafetyEventAsync(
        Guid sessionId,
        string phase,
        string outcome,
        string? category = null,
        int? severity = null,
        string? filterName = null,
        CancellationToken cancellationToken = default);

    // ── Audit ────────────────────────────────────────────────────────────

    /// <summary>
    /// Records an auditable operation for compliance and debugging.
    /// </summary>
    Task RecordAuditAsync(
        string operation,
        string source,
        Guid? sessionId = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    // ── Reads ────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves a paginated list of sessions, optionally filtered by status,
    /// ordered by most recent first.
    /// </summary>
    /// <param name="limit">Maximum number of sessions to return (default 50).</param>
    /// <param name="offset">Number of sessions to skip for pagination (default 0).</param>
    /// <param name="status">
    /// Optional status filter; <see langword="null"/> returns every status. This is the enum and not
    /// a string for the same reason <see cref="EndSessionAsync"/> is: reads and writes have to share
    /// one vocabulary. While it was a string the filter accepted <c>"errored"</c> — a word the schema
    /// has never held — and answered with an empty page that was indistinguishable from "no errored
    /// sessions", which is how #289's swallowed writes stayed invisible on the dashboard.
    /// </param>
    /// <param name="since">Optional lower bound on started_at (inclusive).</param>
    /// <param name="until">Optional upper bound on started_at (exclusive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only list of session records.</returns>
    Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(
        int limit = 50,
        int offset = 0,
        SessionStatus? status = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single session by its unique identifier.
    /// Returns <c>null</c> if the session does not exist.
    /// </summary>
    /// <param name="sessionId">The session's database-assigned ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SessionRecord?> GetSessionByIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all messages for a given session, ordered by turn index.
    /// </summary>
    /// <param name="sessionId">The parent session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SessionMessageRecord>> GetSessionMessagesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all tool execution records for a given session, ordered by creation time.
    /// </summary>
    /// <param name="sessionId">The parent session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ToolExecutionRecord>> GetSessionToolExecutionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single tool execution record scoped to its parent session.
    /// Returns <c>null</c> when not found or when it belongs to a different session.
    /// </summary>
    /// <param name="sessionId">The expected parent session id.</param>
    /// <param name="invocationId">The tool execution row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ToolExecutionRecord?> GetToolExecutionByIdAsync(
        Guid sessionId,
        Guid invocationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single session message scoped to its parent session.
    /// Returns <c>null</c> when not found or when it belongs to a different session.
    /// </summary>
    /// <param name="sessionId">The expected parent session id.</param>
    /// <param name="messageId">The message row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SessionMessageRecord?> GetMessageByIdAsync(
        Guid sessionId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all safety event records for a given session, ordered by creation time.
    /// </summary>
    /// <param name="sessionId">The parent session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SafetyEventRecord>> GetSessionSafetyEventsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    // ── Context Snapshots (Foresight) ───────────────────────────────────

    /// <summary>
    /// Persists a single Foresight context snapshot for a turn. Idempotent on
    /// <c>(ConversationId, TurnIndex)</c>: replays of the same turn overwrite
    /// the prior row rather than creating duplicates.
    /// </summary>
    /// <param name="snapshot">The snapshot to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordContextSnapshotAsync(
        ContextSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the most recent snapshot for a conversation (highest
    /// <c>TurnIndex</c>). Used by the sessions-list aggregate to populate
    /// <c>SessionRecord.Breakdown</c>. Returns <c>null</c> if no snapshots exist.
    /// </summary>
    /// <param name="conversationId">Stable conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ContextSnapshot?> GetLatestSnapshotAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all snapshots for a conversation ordered by <c>TurnIndex</c>.
    /// Used by the session-detail endpoint to rehydrate the per-turn timeline
    /// after a page refresh.
    /// </summary>
    /// <param name="conversationId">Stable conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ContextSnapshot>> GetSnapshotsAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batched lookup: for each supplied conversation id, returns the
    /// <see cref="CategoryBreakdown"/> from that conversation's latest snapshot.
    /// Conversations with no snapshots are omitted from the result. Single query
    /// implementation required — sessions-list paths must not be N+1.
    /// </summary>
    /// <param name="conversationIds">Conversation ids to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, CategoryBreakdown>> GetLatestBreakdownsAsync(
        IEnumerable<string> conversationIds,
        CancellationToken cancellationToken = default);

    // ── Loaded Item Bodies (Foresight sidecar) ──────────────────────────

    /// <summary>
    /// Persists the per-loaded-item body text for a snapshot turn. Idempotent
    /// on <c>(ConversationId, TurnIndex, LoadedIndex)</c>: re-emitting a turn's
    /// bodies overwrites prior rows. Bodies are stored in a sidecar table —
    /// the parent <c>context_snapshots</c> row stays small so SignalR /
    /// HTTP payloads aren't bloated by 5-20 KB system prompts that the UI
    /// only needs on demand.
    /// </summary>
    /// <param name="conversationId">Stable conversation identifier.</param>
    /// <param name="turnIndex">Turn index the bodies belong to.</param>
    /// <param name="bodies">Bodies to persist. May be empty; callers can skip
    /// invoking this method entirely when there's nothing to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordLoadedBodiesAsync(
        string conversationId,
        int turnIndex,
        IReadOnlyList<LoadedItemBody> bodies,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single loaded-item body. Returns <c>null</c> when no body
    /// was captured (e.g. <c>messages</c> category items, or rows from before
    /// the body-capture migration). Powers the drawer's lazy fetch on open.
    /// </summary>
    /// <param name="conversationId">Stable conversation identifier.</param>
    /// <param name="turnIndex">Turn the loaded item belongs to.</param>
    /// <param name="loadedIndex">Position in the snapshot's <c>Loaded[]</c> array.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetLoadedBodyAsync(
        string conversationId,
        int turnIndex,
        int loadedIndex,
        CancellationToken cancellationToken = default);
}
