namespace Domain.AI.Permissions;

/// <summary>
/// Tracks denial history for a specific tool+operation pattern.
/// Used by the rate limiter to auto-escalate to hard deny after
/// repeated denials.
/// </summary>
public sealed record DenialRecord
{
    /// <summary>The tool name that was denied.</summary>
    public required string ToolName { get; init; }

    /// <summary>The operation pattern that was denied (null for tool-level denials).</summary>
    public string? OperationPattern { get; init; }

    /// <summary>Total number of denials for this pattern.</summary>
    public required int DenialCount { get; init; }

    /// <summary>When the first denial occurred.</summary>
    public required DateTimeOffset FirstDenied { get; init; }

    /// <summary>When the most recent denial occurred.</summary>
    public required DateTimeOffset LastDenied { get; init; }
}
