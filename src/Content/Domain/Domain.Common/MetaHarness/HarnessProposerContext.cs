namespace Domain.Common.MetaHarness;

/// <summary>
/// Immutable input context passed to <see cref="Application.AI.Common.Interfaces.MetaHarness.IHarnessProposer"/>
/// for a single propose step within an optimization run.
/// </summary>
public sealed record HarnessProposerContext
{
    /// <summary>The candidate configuration to improve upon.</summary>
    public required HarnessCandidate CurrentCandidate { get; init; }

    /// <summary>
    /// Absolute path to the <c>optimizations/{optRunId}/</c> directory.
    /// Acts as the filesystem sandbox root for the proposer agent.
    /// </summary>
    public required string OptimizationRunDirectoryPath { get; init; }

    /// <summary>
    /// All prior candidate IDs in this run, ordered oldest-first.
    /// Used by the proposer to navigate trace subdirectories.
    /// </summary>
    public required IReadOnlyList<Guid> PriorCandidateIds { get; init; }

    /// <summary>Zero-based iteration index within the optimization run.</summary>
    public required int Iteration { get; init; }

    /// <summary>
    /// Accumulated text content of <c>learnings.md</c> from all prior iterations.
    /// <c>null</c> on the first iteration or when no learnings file has been written yet.
    /// Used to prevent the proposer from re-attempting failed hypotheses.
    /// </summary>
    public string? PriorLearnings { get; init; }
}
