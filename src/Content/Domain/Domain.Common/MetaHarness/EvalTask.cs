namespace Domain.Common.MetaHarness;

/// <summary>
/// A single evaluation task used to score a <see cref="HarnessCandidate"/>.
/// Loaded from JSON files under <c>MetaHarnessConfig.EvalTasksPath</c>.
/// </summary>
public sealed record EvalTask
{
    /// <summary>Stable unique identifier for this task (used in trace paths).</summary>
    public required string TaskId { get; init; }

    /// <summary>Human-readable description of what the task exercises.</summary>
    public required string Description { get; init; }

    /// <summary>The prompt sent to the agent under evaluation.</summary>
    public required string InputPrompt { get; init; }

    /// <summary>
    /// Optional .NET regex pattern. Agent output must match for the task to pass.
    /// Null means the task is always considered passed (useful for smoke tests).
    /// </summary>
    public string? ExpectedOutputPattern { get; init; }

    /// <summary>Arbitrary tags for filtering (e.g., "smoke", "regression").</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
