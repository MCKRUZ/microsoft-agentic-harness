using Application.AI.Common.Interfaces.Tools;

namespace Application.AI.Common.Models.Tools;

/// <summary>
/// A single tool execution request within a batch. Pairs a resolved <see cref="ITool"/>
/// with the operation and parameters the LLM requested, plus a unique call ID for
/// correlation with <see cref="ToolExecutionResult"/> and progress tracking.
/// </summary>
public sealed record ToolExecutionRequest
{
    /// <summary>The resolved tool instance to execute.</summary>
    public required ITool Tool { get; init; }

    /// <summary>The operation to perform on the tool (must be in <see cref="ITool.SupportedOperations"/>).</summary>
    public required string Operation { get; init; }

    /// <summary>The parameters for the operation, deserialized from the LLM's JSON arguments.</summary>
    public required IReadOnlyDictionary<string, object?> Parameters { get; init; }

    /// <summary>Unique identifier for this call within the batch, used for progress tracking and result correlation.</summary>
    public required string CallId { get; init; }
}
