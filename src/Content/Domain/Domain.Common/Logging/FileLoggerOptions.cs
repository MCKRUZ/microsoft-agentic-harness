namespace Domain.Common.Logging;

/// <summary>
/// Configuration options for file-based logging providers.
/// </summary>
public class FileLoggerOptions
{
    /// <summary>
    /// Gets or sets the base directory for log file output.
    /// </summary>
    public string? LogsBasePath { get; set; }

    /// <summary>
    /// Gets or sets the current run identifier. Set when a new run starts.
    /// </summary>
    public string? CurrentRunId { get; set; }
}
