namespace Presentation.LoggerUI;

/// <summary>
/// Configuration for a log level including display name, color, and icon.
/// </summary>
internal record LogLevelConfig(string Name, string Color, string Icon);

/// <summary>
/// Represents a parsed log entry.
/// </summary>
internal sealed class LogEntry
{
    public string Raw { get; set; } = string.Empty;
    public string? Level { get; set; }
    public DateTime? Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsStructured { get; set; }
    public bool HasException { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
}

/// <summary>
/// Command line options for the logger.
/// </summary>
internal sealed class LoggerOptions
{
    private static readonly string[] DefaultPipeNames =
    {
        "AgenticHarnessLogs.ConsoleUI",
        "AgenticHarnessLogs.AgentHub"
    };

    public List<string> PipeNames { get; set; } = new();
    public bool ParseJson { get; set; } = true;

    /// <summary>
    /// Parses command line arguments into logger options.
    /// Collects pipe names from positional args or --pipe/-p flags;
    /// defaults to the standard harness pipe names if none supplied.
    /// </summary>
    internal static LoggerOptions FromArgs(string[] args)
    {
        var pipes = new List<string>();
        var parseJson = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--pipe":
                case "-p":
                    if (i + 1 < args.Length)
                        pipes.Add(args[++i]);
                    break;
                case "--no-json":
                    parseJson = false;
                    break;
                default:
                    if (!args[i].StartsWith("--"))
                        pipes.Add(args[i]);
                    break;
            }
        }

        if (pipes.Count == 0)
            pipes.AddRange(DefaultPipeNames);

        return new LoggerOptions
        {
            PipeNames = pipes,
            ParseJson = parseJson
        };
    }
}
