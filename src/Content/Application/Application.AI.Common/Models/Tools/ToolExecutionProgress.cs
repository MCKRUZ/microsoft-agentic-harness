namespace Application.AI.Common.Models.Tools;

/// <summary>
/// Progress report for a tool execution within a batch.
/// Reported via <see cref="IProgress{T}"/> during <see cref="Interfaces.Tools.IToolExecutionStrategy.ExecuteBatchAsync"/>.
/// </summary>
public sealed record ToolExecutionProgress
{
    /// <summary>The call ID of the tool being reported on.</summary>
    public required string CallId { get; init; }

    /// <summary>Current status description (e.g., "executing", "completed", "failed").</summary>
    public required string Status { get; init; }

    /// <summary>Optional completion percentage (0.0 to 1.0). Null when indeterminate.</summary>
    public double? PercentComplete { get; init; }
}
