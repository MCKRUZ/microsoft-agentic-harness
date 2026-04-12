namespace Domain.Common.MetaHarness;

/// <summary>
/// Represents the contents of <c>scores.json</c>.
/// Immutable record; no behavior beyond what <c>record</c> provides.
/// </summary>
public sealed record HarnessScores
{
    /// <summary>Pass rate in the range 0.0–1.0.</summary>
    public double PassRate { get; init; }

    /// <summary>Cumulative token cost for this run.</summary>
    public long TotalTokenCost { get; init; }

    /// <summary>Per-task pass/fail breakdown.</summary>
    public IReadOnlyList<ExampleResult> PerExampleResults { get; init; } =
        Array.Empty<ExampleResult>();

    /// <summary>When scoring was completed.</summary>
    public DateTimeOffset ScoredAt { get; init; }
}

/// <summary>Per-task pass/fail result within <see cref="HarnessScores"/>.</summary>
public sealed record ExampleResult
{
    /// <summary>Identifies which eval task this result belongs to.</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>Whether the task passed evaluation.</summary>
    public bool Passed { get; init; }

    /// <summary>Tokens consumed during this task's execution.</summary>
    public long TokenCost { get; init; }
}
