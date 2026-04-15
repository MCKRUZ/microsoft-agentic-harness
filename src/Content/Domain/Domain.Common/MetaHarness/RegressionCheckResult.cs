namespace Domain.Common.MetaHarness;

/// <summary>
/// Result of checking a candidate's evaluation results against the regression suite.
/// Produced by <c>IRegressionSuiteService.Check</c>.
/// </summary>
public sealed record RegressionCheckResult
{
    /// <summary>
    /// Whether the candidate passed the regression gate.
    /// Always <c>true</c> when the suite is empty.
    /// </summary>
    public required bool Passed { get; init; }

    /// <summary>
    /// Fraction [0.0, 1.0] of regression suite tasks that the candidate passed.
    /// <c>1.0</c> when the suite is empty.
    /// </summary>
    public required double PassRate { get; init; }

    /// <summary>
    /// Regression suite task IDs that the candidate failed or that were absent from
    /// the evaluation results (treated as failed — conservative).
    /// Empty when the gate passed or the suite is empty.
    /// </summary>
    public required IReadOnlyList<string> FailedTaskIds { get; init; }
}
