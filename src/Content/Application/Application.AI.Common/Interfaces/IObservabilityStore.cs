namespace Application.AI.Common.Interfaces;

/// <summary>
/// Persists structured observability data (sessions, messages, tool executions,
/// safety events, and audit entries) to a durable store for historical analytics
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
}
