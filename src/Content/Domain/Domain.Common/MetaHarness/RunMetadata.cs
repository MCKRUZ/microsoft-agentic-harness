namespace Domain.Common.MetaHarness;

/// <summary>
/// Metadata written to <c>manifest.json</c> when a run is started.
/// Immutable record; no behavior beyond what <c>record</c> provides.
/// </summary>
public sealed record RunMetadata
{
    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Name of the agent being traced.</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>Optional human-readable description of the task.</summary>
    public string? TaskDescription { get; init; }

    /// <summary>Set for optimization eval runs.</summary>
    public Guid? CandidateId { get; init; }

    /// <summary>Set for optimization eval runs.</summary>
    public Guid? OptimizationRunId { get; init; }

    /// <summary>Set for optimization eval runs.</summary>
    public int? Iteration { get; init; }

    /// <summary>Set for eval task runs.</summary>
    public string? TaskId { get; init; }
}
