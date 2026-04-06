namespace Application.Common.Logging;

/// <summary>
/// Configuration options for file-based logging providers
/// (<see cref="FileLoggerProvider"/> and <see cref="StructuredJsonLoggerProvider"/>).
/// </summary>
// Mutable setters are intentional: CurrentRunId is set at runtime when a new agent
// run starts, and LogsBasePath may be configured via ILoggingBuilder.AddFileLogger().
// This class is registered as a singleton — treat as mutate-once-at-startup for LogsBasePath,
// mutate-per-run for CurrentRunId.
public class FileLoggerOptions
{
    /// <summary>
    /// Gets or sets the base directory for log file output.
    /// Each run creates a timestamped subdirectory beneath this path.
    /// </summary>
    /// <value>Default: <c>null</c>. When null, file-based loggers will not write output.</value>
    public string? LogsBasePath { get; set; }

    /// <summary>
    /// Gets or sets the current run identifier. Set when a new agent run starts.
    /// Used to create run-specific subdirectories under <see cref="LogsBasePath"/>.
    /// </summary>
    public string? CurrentRunId { get; set; }
}
