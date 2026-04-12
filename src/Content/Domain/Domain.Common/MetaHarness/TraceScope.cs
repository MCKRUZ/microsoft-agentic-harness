namespace Domain.Common.MetaHarness;

/// <summary>
/// Encodes the three-tier identity (OptimizationRun → Candidate → Execution) and
/// resolves filesystem paths for trace output. Immutable record; no I/O.
/// </summary>
public sealed record TraceScope
{
    /// <summary>Always required. Identifies a single agent execution.</summary>
    public Guid ExecutionRunId { get; init; }

    /// <summary>The outer loop run. Null for non-optimization agent runs.</summary>
    public Guid? OptimizationRunId { get; init; }

    /// <summary>One proposed harness configuration. Null for non-optimization runs.</summary>
    public Guid? CandidateId { get; init; }

    /// <summary>Which eval task this execution belongs to. Null for non-eval runs.</summary>
    public string? TaskId { get; init; }

    /// <summary>Creates a standalone execution scope (non-optimization agent run).</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="executionRunId"/> is <see cref="Guid.Empty"/>.</exception>
    public static TraceScope ForExecution(Guid executionRunId)
    {
        if (executionRunId == Guid.Empty)
            throw new ArgumentException("ExecutionRunId must not be empty.", nameof(executionRunId));
        return new() { ExecutionRunId = executionRunId };
    }

    /// <summary>
    /// Returns the absolute directory path for this scope under <paramref name="traceRoot"/>.
    /// Pure string operation — no I/O.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="CandidateId"/> is set without <see cref="OptimizationRunId"/>,
    /// or when <see cref="TaskId"/> is set without <see cref="CandidateId"/>.
    /// </exception>
    public string ResolveDirectory(string traceRoot)
    {
        if (CandidateId.HasValue && !OptimizationRunId.HasValue)
            throw new InvalidOperationException("CandidateId requires OptimizationRunId.");
        if (TaskId is not null && !CandidateId.HasValue)
            throw new InvalidOperationException("TaskId requires CandidateId.");

        // Guard against path traversal: TaskId is used as a directory segment
        if (TaskId is not null)
        {
            if (TaskId.Contains("..") || TaskId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || TaskId.Contains('/') || TaskId.Contains('\\'))
                throw new ArgumentException(
                    $"TaskId contains invalid path characters: '{TaskId}'", nameof(TaskId));
        }

        if (!OptimizationRunId.HasValue)
            return Path.Combine(traceRoot, "executions", ExecutionRunId.ToString("D").ToLowerInvariant());

        var optPath = Path.Combine(traceRoot, "optimizations", OptimizationRunId.Value.ToString("D").ToLowerInvariant());

        if (!CandidateId.HasValue)
            return optPath;

        var candidatePath = Path.Combine(optPath, "candidates", CandidateId.Value.ToString("D").ToLowerInvariant());

        if (TaskId is null)
            return candidatePath;

        return Path.Combine(candidatePath, "eval", TaskId, ExecutionRunId.ToString("D").ToLowerInvariant());
    }
}
