using Application.AI.Common.Interfaces;

namespace Infrastructure.Observability.Persistence;

/// <summary>
/// No-op implementation of <see cref="IObservabilityStore"/> used when
/// PostgreSQL persistence is disabled. All methods return immediately
/// without recording any data.
/// </summary>
public sealed class NullObservabilityStore : IObservabilityStore
{
    public Task<Guid> StartSessionAsync(
        string conversationId, string agentName, string? model,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    public Task EndSessionAsync(
        Guid sessionId, string status, string? errorMessage,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UpdateSessionMetricsAsync(
        Guid sessionId, int turnCount, int toolCallCount, int subagentCount,
        int totalInputTokens, int totalOutputTokens, int totalCacheRead,
        int totalCacheWrite, decimal totalCostUsd, decimal cacheHitRate,
        string? model = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<Guid> RecordMessageAsync(
        Guid sessionId, int turnIndex, string role, string? source,
        string? contentPreview, string? model, int inputTokens, int outputTokens,
        int cacheRead, int cacheWrite, decimal costUsd, decimal cacheHitPct,
        string[]? toolNames = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    public Task RecordToolExecutionAsync(
        Guid sessionId, Guid? messageId, string toolName, string toolSource,
        int durationMs, string status, string? errorType,
        int? resultSize, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RecordSafetyEventAsync(
        Guid sessionId, string phase, string outcome, string? category,
        int? severity, string? filterName,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RecordAuditAsync(
        string operation, string source, Guid? sessionId,
        Dictionary<string, object>? metadata,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
