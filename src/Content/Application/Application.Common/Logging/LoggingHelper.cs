using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// Shared formatting utilities consumed by all logging providers and formatters.
/// Centralizes display conventions so log output is consistent across console,
/// file, structured JSON, and named pipe destinations.
/// </summary>
public static partial class LoggingHelper
{
    /// <summary>
    /// ANSI color codes used by console formatters.
    /// </summary>
    public static class AnsiColors
    {
        /// <summary>Reset to default terminal color.</summary>
        public const string Reset = "\x1B[39m\x1B[22m";
        /// <summary>Red — used for Error level.</summary>
        public const string Red = "\x1B[31m";
        /// <summary>Yellow — used for Warning level.</summary>
        public const string Yellow = "\x1B[33m";
        /// <summary>Green — used for Information level.</summary>
        public const string Green = "\x1B[32m";
        /// <summary>Cyan — used for Debug level.</summary>
        public const string Cyan = "\x1B[36m";
        /// <summary>Gray — used for Trace level and muted content.</summary>
        public const string Gray = "\x1B[90m";
        /// <summary>Magenta — used for Critical level.</summary>
        public const string Magenta = "\x1B[35m";
        /// <summary>Blue — used for agent identity.</summary>
        public const string Blue = "\x1B[34m";
        /// <summary>Bold modifier.</summary>
        public const string Bold = "\x1B[1m";
    }

    /// <summary>
    /// Stable ANSI colors for agent identity assignment.
    /// Each agent gets a consistent color across the session based on its ID hash.
    /// </summary>
    private static readonly string[] AgentColors =
    [
        "\x1B[32m", // Green
        "\x1B[33m", // Yellow
        "\x1B[34m", // Blue
        "\x1B[35m", // Magenta
        "\x1B[36m", // Cyan
        "\x1B[91m", // Bright Red
        "\x1B[92m", // Bright Green
        "\x1B[93m", // Bright Yellow
        "\x1B[94m", // Bright Blue
        "\x1B[95m", // Bright Magenta
        "\x1B[96m", // Bright Cyan
    ];

    /// <summary>
    /// Returns a 4-character padded abbreviation for the given log level.
    /// </summary>
    /// <param name="logLevel">The log level to abbreviate.</param>
    /// <returns>A fixed-width string suitable for column-aligned log output.</returns>
    /// <example>
    /// <code>
    /// LoggingHelper.GetShortLevel(LogLevel.Information); // "INFO"
    /// LoggingHelper.GetShortLevel(LogLevel.Warning);     // "WARN"
    /// </code>
    /// </example>
    public static string GetShortLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERR ",
        LogLevel.Critical => "CRIT",
        _ => "UNKN"
    };

    /// <summary>
    /// Returns a lowercase name for the given log level, suitable for structured output.
    /// </summary>
    /// <param name="logLevel">The log level to name.</param>
    /// <returns>A lowercase string (e.g., "info", "warn", "error").</returns>
    public static string GetLevelName(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "unknown"
    };

    /// <summary>
    /// Returns the ANSI color code for the given log level.
    /// </summary>
    /// <param name="logLevel">The log level to colorize.</param>
    /// <returns>An ANSI escape sequence string.</returns>
    public static string GetLevelColor(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => AnsiColors.Gray,
        LogLevel.Debug => AnsiColors.Cyan,
        LogLevel.Information => AnsiColors.Green,
        LogLevel.Warning => AnsiColors.Yellow,
        LogLevel.Error => AnsiColors.Red,
        LogLevel.Critical => AnsiColors.Magenta,
        _ => AnsiColors.Reset
    };

    /// <summary>
    /// Extracts the short class name from a fully-qualified logger category.
    /// </summary>
    /// <param name="category">The fully-qualified category name (e.g., "Application.Core.Services.MyService").</param>
    /// <returns>The last segment after the final dot (e.g., "MyService").</returns>
    public static string GetShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }

    /// <summary>
    /// Returns a display name for an agent, using parent-child notation for subagents.
    /// </summary>
    /// <param name="agentId">The agent's identifier.</param>
    /// <param name="parentAgentId">The parent agent's identifier, or <c>null</c> for root agents.</param>
    /// <returns>A formatted string like "main" or "main&gt;research".</returns>
    /// <example>
    /// <code>
    /// LoggingHelper.GetAgentDisplayName("research", "main");  // "main>research"
    /// LoggingHelper.GetAgentDisplayName("planner", null);     // "planner"
    /// </code>
    /// </example>
    public static string GetAgentDisplayName(string agentId, string? parentAgentId) =>
        parentAgentId is null
            ? agentId
            : DisplayNameCache.GetOrAdd((agentId, parentAgentId),
                static key => $"{key.ParentId}>{key.AgentId}");

    private static readonly ConcurrentDictionary<(string AgentId, string? ParentId), string> DisplayNameCache = new();

    /// <summary>
    /// Formats a token count as a compact human-readable string.
    /// </summary>
    /// <param name="tokens">The token count to format.</param>
    /// <returns>A compact string like "128", "1.2k", or "15.3k".</returns>
    public static string FormatTokenCount(long tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(),
        < 10_000 => $"{tokens / 1_000.0:F1}k",
        < 1_000_000 => $"{tokens / 1_000.0:F0}k",
        _ => $"{tokens / 1_000_000.0:F1}M"
    };

    /// <summary>
    /// Formats a duration as a compact human-readable string optimized for
    /// the sub-second to multi-minute range typical of tool executions.
    /// </summary>
    /// <param name="duration">The time span to format.</param>
    /// <returns>A compact string like "45ms", "1.2s", or "2m03s".</returns>
    public static string FormatToolDuration(TimeSpan duration) => duration.TotalMilliseconds switch
    {
        < 1 => "<1ms",
        < 1_000 => $"{duration.TotalMilliseconds:F0}ms",
        < 60_000 => $"{duration.TotalSeconds:F1}s",
        _ => $"{(int)duration.TotalMinutes}m{duration.Seconds:D2}s"
    };

    /// <summary>
    /// Returns a deterministic ANSI color code for the given agent ID, ensuring
    /// the same agent always renders with the same color across sessions.
    /// Uses FNV-1a hashing for cross-process determinism (unlike <c>string.GetHashCode</c>
    /// which is randomized per process in .NET Core).
    /// </summary>
    /// <param name="agentId">The agent identifier to colorize.</param>
    /// <returns>An ANSI escape sequence string.</returns>
    public static string GetStableAgentColor(string agentId)
    {
        // FNV-1a hash for deterministic cross-process results
        var hash = 2166136261u;
        foreach (var c in agentId)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return AgentColors[hash % (uint)AgentColors.Length];
    }

    /// <summary>
    /// Truncates and sanitizes a string for safe inclusion in log output.
    /// Strips control characters (including ANSI escape sequences) and enforces
    /// a maximum length to prevent large tool outputs from bloating log files.
    /// Truncates before scanning to avoid processing characters that will be discarded.
    /// </summary>
    /// <param name="value">The string to sanitize. Returns empty string for <c>null</c>.</param>
    /// <param name="maxLength">Maximum character length before truncation. Default: 200.</param>
    /// <returns>A sanitized, potentially truncated string.</returns>
    public static string SanitizeForLog(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var truncated = value.Length <= maxLength ? value : value[..maxLength];

        // Fast-path: scan for control chars before invoking regex
        var hasControl = false;
        foreach (var c in truncated)
        {
            if (c < 0x20 || c == 0x7F) { hasControl = true; break; }
        }

        var sanitized = hasControl ? ControlCharRegex().Replace(truncated, " ") : truncated;
        return value.Length <= maxLength ? sanitized : string.Concat(sanitized, "...[truncated]");
    }

    /// <summary>
    /// Extracts the message body from a formatted log string, stripping the
    /// category and event ID prefix that <c>ILogger</c> prepends.
    /// </summary>
    /// <param name="formatted">The full formatted log string.</param>
    /// <returns>The message body without the category prefix.</returns>
    public static string ExtractMessageFromFormatted(string formatted)
    {
        var match = CategoryPrefixRegex().Match(formatted);
        return match.Success ? formatted[(match.Length)..] : formatted;
    }

    [GeneratedRegex(@"[\x00-\x1F\x7F]", RegexOptions.None)]
    private static partial Regex ControlCharRegex();

    [GeneratedRegex(@"^[^\[]*\[\d+\]\s*", RegexOptions.None)]
    private static partial Regex CategoryPrefixRegex();
}
