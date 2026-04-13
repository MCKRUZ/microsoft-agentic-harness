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
    /// <see cref="Domain.Common.MetaHarness.HarnessCandidate.CandidateId"/> of the
    /// best-scoring candidate, or null when no candidates were evaluated.
    /// </summary>
    public Guid? BestCandidateId { get; init; }

    /// <summary>Pass rate [0.0, 1.0] of the best candidate; 0.0 when no candidates evaluated.</summary>
    public double BestScore { get; init; }

    /// <summary>Total number of iterations executed (including failure iterations).</summary>
    public int IterationCount { get; init; }

    /// <summary>
    /// Absolute path to the <c>_proposed/</c> directory containing the best candidate's snapshot.
    /// Empty string when no iterations completed successfully.
    /// </summary>
    public required string ProposedChangesPath { get; init; }
}
