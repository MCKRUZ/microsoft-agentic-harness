using Application.AI.Common.Interfaces;
using Domain.AI.Observability.Models;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// No-op implementation of <see cref="IObservabilityStore"/> used when
/// PostgreSQL persistence is disabled. All write methods return immediately
/// without recording any data. All read methods return empty collections.
/// </summary>
public sealed class NullObservabilityStore : IObservabilityStore
{
    /// <inheritdoc />
    public Task<Guid> StartSessionAsync(
        string conversationId, string agentName, string? model,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    /// <inheritdoc />
    public Task EndSessionAsync(
        Guid sessionId, string status, string? errorMessage,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateSessionMetricsAsync(
        Guid sessionId, int turnCount, int toolCallCount, int subagentCount,
        int totalInputTokens, int totalOutputTokens, int totalCacheRead,
        int totalCacheWrite, decimal totalCostUsd, decimal cacheHitRate,
        string? model = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<Guid> RecordMessageAsync(
        Guid sessionId, int turnIndex, string role, string? source,
        string? contentPreview, string? model, int inputTokens, int outputTokens,
        int cacheRead, int cacheWrite, decimal costUsd, decimal cacheHitPct,
        string[]? toolNames = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    /// <inheritdoc />
    public Task RecordToolExecutionAsync(
        Guid sessionId, Guid? messageId, string toolName, string toolSource,
        int durationMs, string status, string? errorType,
        int? resultSize, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RecordSafetyEventAsync(
        Guid sessionId, string phase, string outcome, string? category,
        int? severity, string? filterName,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RecordAuditAsync(
        string operation, string source, Guid? sessionId,
        Dictionary<string, object>? metadata,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(
        int limit = 50, int offset = 0, string? status = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionRecord>>(Array.Empty<SessionRecord>());

    /// <inheritdoc />
    public Task<SessionRecord?> GetSessionByIdAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<SessionRecord?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionMessageRecord>> GetSessionMessagesAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionMessageRecord>>(Array.Empty<SessionMessageRecord>());

    /// <inheritdoc />
    public Task<IReadOnlyList<ToolExecutionRecord>> GetSessionToolExecutionsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>(Array.Empty<ToolExecutionRecord>());

    /// <inheritdoc />
    public Task<IReadOnlyList<SafetyEventRecord>> GetSessionSafetyEventsAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SafetyEventRecord>>(Array.Empty<SafetyEventRecord>());
}
