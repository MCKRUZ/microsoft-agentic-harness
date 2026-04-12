namespace Domain.Common.MetaHarness;

/// <summary>
/// Immutable domain record representing one proposed harness configuration within an
/// optimization run. Status transitions are performed via <c>with</c> expressions.
/// </summary>
public sealed record HarnessCandidate
{
    /// <summary>Stable unique identifier for this candidate.</summary>
    public required Guid CandidateId { get; init; }

    /// <summary>The optimization run this candidate belongs to.</summary>
    public required Guid OptimizationRunId { get; init; }

    /// <summary>
    /// Null for the seed candidate; set to the parent's <see cref="CandidateId"/> for all proposals.
    /// </summary>
    public Guid? ParentCandidateId { get; init; }

    /// <summary>Zero-based iteration index within the optimization run.</summary>
    public required int Iteration { get; init; }

    /// <summary>UTC timestamp when this candidate was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The harness configuration snapshot this candidate represents.</summary>
    public required HarnessSnapshot Snapshot { get; init; }

    /// <summary>Pass rate [0.0, 1.0] after evaluation. Null until evaluated.</summary>
    public double? BestScore { get; init; }

    /// <summary>Cumulative LLM token cost across all eval task runs. Null until evaluated.</summary>
    public long? TokenCost { get; init; }

    /// <summary>Current lifecycle state of this candidate.</summary>
    public required HarnessCandidateStatus Status { get; init; }

    /// <summary>
    /// Human-readable failure message. Only set when <see cref="Status"/> is
    /// <see cref="HarnessCandidateStatus.Failed"/>.
    /// </summary>
    public string? FailureReason { get; init; }
}
