using Domain.AI.Models;

namespace Application.AI.Common.Models.Tools;

/// <summary>
/// The result of a single tool execution within a batch. Pairs the original
/// <see cref="CallId"/> with the tool's <see cref="ToolResult"/> and execution metadata
/// for telemetry and error reporting.
/// </summary>
public sealed record ToolExecutionResult
{
    /// <summary>The call ID matching the originating <see cref="ToolExecutionRequest"/>.</summary>
    public required string CallId { get; init; }

    /// <summary>The tool result containing success output or failure error.</summary>
    public required ToolResult Result { get; init; }

    /// <summary>Whether execution completed without throwing an unhandled exception.</summary>
    public required bool Completed { get; init; }

    /// <summary>
    /// Telemetry-safe error category string (e.g., "timeout", "permission_denied").
    /// Null when execution completed successfully.
    /// </summary>
    public string? ErrorCategory { get; init; }
}
