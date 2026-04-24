namespace Domain.AI.Observability.Models;

/// <summary>
/// Represents a single tool invocation within a session.
/// </summary>
public sealed record ToolExecutionRecord
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public Guid? MessageId { get; init; }
    public required string ToolName { get; init; }
    public string? ToolSource { get; init; }
    public int? DurationMs { get; init; }
    public required string Status { get; init; }
    public string? ErrorType { get; init; }
    public int? ResultSize { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
