namespace Domain.AI.Observability.Models;

/// <summary>
/// Represents a persisted agent conversation session with aggregate metrics.
/// </summary>
public sealed record SessionRecord
{
    public Guid Id { get; init; }
    public required string ConversationId { get; init; }
    public required string AgentName { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? DurationMs { get; init; }
    public int TurnCount { get; init; }
    public int ToolCallCount { get; init; }
    public int SubagentCount { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public int TotalCacheRead { get; init; }
    public int TotalCacheWrite { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal CacheHitRate { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
