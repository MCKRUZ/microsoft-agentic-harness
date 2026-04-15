namespace Domain.Common.MetaHarness;

/// <summary>
/// Immutable set of eval task IDs that must continue passing before a new candidate
/// can be accepted as the best. Tasks are promoted into this suite automatically when
/// they transition from failing to passing in a winning iteration.
/// </summary>
/// <remarks>
/// The suite is self-maintained: the outer optimization loop populates it over time
/// rather than requiring manual curation. An empty suite always passes the gate.
/// Persisted as <c>regression_suite.json</c> in the optimization run directory.
/// </remarks>
public sealed record RegressionSuite
{
    /// <summary>
    /// Eval task IDs that constitute the regression gate.
    /// All must achieve at least <see cref="Threshold"/> pass rate for the gate to pass.
    /// </summary>
    public required IReadOnlyList<string> TaskIds { get; init; }

    /// <summary>
    /// Minimum fraction [0.0, 1.0] of <see cref="TaskIds"/> that must pass
    /// for a candidate to be accepted as best.
    /// </summary>
    public required double Threshold { get; init; }

    /// <summary>UTC timestamp of the last promotion event that added tasks to this suite.</summary>
    public required DateTimeOffset LastUpdatedAt { get; init; }
}
