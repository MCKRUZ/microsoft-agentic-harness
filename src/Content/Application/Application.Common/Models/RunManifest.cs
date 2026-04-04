namespace Application.Common.Models;

/// <summary>
/// Represents metadata about a completed logging run, serialized as JSON
/// alongside the log files for post-hoc analysis and tooling integration.
/// </summary>
/// <remarks>
/// Written by <c>FileLoggerProvider.CompleteRun()</c> to the run directory.
/// Tools and dashboards can read this manifest to understand what happened
/// during the run without parsing log files.
/// </remarks>
public record RunManifest
{
    /// <summary>Gets the unique identifier for this run.</summary>
    public required string RunId { get; init; }

    /// <summary>Gets the phase or stage of execution (e.g., "planning", "execution", "review").</summary>
    public string? Phase { get; init; }

    /// <summary>Gets the UTC timestamp when the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets the UTC timestamp when the run completed.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets the timing breakdown for the run's major stages.</summary>
    public TimingBreakdown? Timing { get; init; }

    /// <summary>Gets the total number of log entries written during the run.</summary>
    public int LogEntryCount { get; init; }

    /// <summary>Gets the correlation ID for distributed tracing, if available.</summary>
    public string? ActivityId { get; init; }
}

/// <summary>
/// Timing breakdown for the major stages of an execution run.
/// </summary>
public record TimingBreakdown
{
    /// <summary>Gets the time spent assembling context (system prompt, tools, skills).</summary>
    public TimeSpan ContextAssembly { get; init; }

    /// <summary>Gets the time spent in LLM generation (API call latency).</summary>
    public TimeSpan Generation { get; init; }

    /// <summary>Gets the time spent executing tools.</summary>
    public TimeSpan ToolExecution { get; init; }

    /// <summary>Gets the total elapsed time for the run.</summary>
    public TimeSpan Total { get; init; }
}
