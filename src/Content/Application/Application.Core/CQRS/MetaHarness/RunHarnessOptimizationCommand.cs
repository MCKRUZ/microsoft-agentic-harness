using MediatR;

namespace Application.Core.CQRS.MetaHarness;

/// <summary>
/// MediatR command that starts or resumes a full meta-harness optimization run.
/// Validated by <see cref="RunHarnessOptimizationCommandValidator"/>.
/// Handled by <see cref="RunHarnessOptimizationCommandHandler"/>.
/// </summary>
public sealed record RunHarnessOptimizationCommand : IRequest<OptimizationResult>
{
    /// <summary>
    /// Identifies this optimization run. Must not be <see cref="Guid.Empty"/>.
    /// Used to name the trace directory and group all candidate records.
    /// </summary>
    public required Guid OptimizationRunId { get; init; }

    /// <summary>
    /// Optional: resume from a prior candidate's snapshot rather than the currently active harness.
    /// When null, the seed snapshot is built from the live configuration via
    /// <see cref="Application.AI.Common.Interfaces.MetaHarness.ISnapshotBuilder"/>.
    /// </summary>
    public Guid? SeedCandidateId { get; init; }

    /// <summary>
    /// Optional override for
    /// <see cref="Domain.Common.Config.MetaHarness.MetaHarnessConfig.MaxIterations"/>.
    /// When provided, must be greater than zero.
    /// </summary>
    public int? MaxIterations { get; init; }
}

/// <summary>
/// Result of a completed optimization run.
/// </summary>
public sealed record OptimizationResult
{
    /// <summary>The optimization run that produced this result.</summary>
    public required Guid OptimizationRunId { get; init; }

    /// <summary>
    /// <see cref="Domain.Common.MetaHarness.HarnessCandidate.CandidateId"/> of the best accepted
    /// proposal, or null when no proposal beat the seed and cleared the regression gate. The seed
    /// itself is never reported here: it is the unchanged starting point, not a change to propose.
    /// </summary>
    public Guid? BestCandidateId { get; init; }

    /// <summary>
    /// Pass rate [0.0, 1.0] of the candidate named by <see cref="BestCandidateId"/>; 0.0 when it is
    /// null. See <see cref="SeedScore"/> for what the starting point scored.
    /// </summary>
    public double BestScore { get; init; }

    /// <summary>
    /// Pass rate [0.0, 1.0] of the seed candidate, measured by this invocation as the baseline every
    /// proposal had to beat. Null when the seed was not evaluated: no eval tasks were found, or a
    /// resumed run restored the best it had already accepted and compared against that instead.
    /// </summary>
    public double? SeedScore { get; init; }

    /// <summary>Total number of iterations executed (including failure iterations).</summary>
    public int IterationCount { get; init; }

    /// <summary>
    /// Absolute path to the <c>_proposed/</c> directory containing the best candidate's snapshot.
    /// Empty string when no iterations completed successfully.
    /// </summary>
    public required string ProposedChangesPath { get; init; }

    /// <summary>
    /// When non-null, the loop exited before reaching <c>MaxIterations</c>.
    /// Value is <c>"no_improvement"</c> when the consecutive no-improvement limit was reached.
    /// </summary>
    public string? EarlyStopReason { get; init; }
}
