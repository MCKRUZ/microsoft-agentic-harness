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
    /// Marks a session as completed or errored and records its final duration.
    /// </summary>
    Task EndSessionAsync(
        Guid sessionId,
        string status,
        string? errorMessage = null,
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
        CancellationToken cancellationToken = default);

    // ── Tool Executions ──────────────────────────────────────────────────

    /// <summary>
    /// Records a single tool invocation with its outcome and performance data.
    /// </summary>
    Task RecordToolExecutionAsync(
        Guid sessionId,
        Guid? messageId,
        string toolName,
        string toolSource,
        int durationMs,
        string status,
        string? errorType = null,
        int? resultSize = null,
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
    /// <param name="status">Optional status filter (e.g. "completed", "errored").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Read-only list of session records.</returns>
    Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(
        int limit = 50,
        int offset = 0,
        string? status = null,
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
    /// Retrieves all safety event records for a given session, ordered by creation time.
    /// </summary>
    /// <param name="sessionId">The parent session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SafetyEventRecord>> GetSessionSafetyEventsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
