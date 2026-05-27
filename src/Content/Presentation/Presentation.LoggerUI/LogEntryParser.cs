using System.Text.Json;
using System.Text.RegularExpressions;

namespace Presentation.LoggerUI;

/// <summary>
/// Parses raw log lines into structured <see cref="LogEntry"/> instances.
/// Supports both JSON structured logs and text-based log formats.
/// </summary>
internal static class LogEntryParser
{
    internal static readonly Dictionary<string, LogLevelConfig> LogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trce"] = new LogLevelConfig("TRCE", "grey69", ""),
        ["dbug"] = new LogLevelConfig("DBG ", "grey", ""),
        ["info"] = new LogLevelConfig("INFO", "cornflowerblue", ""),
        ["warn"] = new LogLevelConfig("WARN", "yellow", "⚠"),
        ["error"] = new LogLevelConfig("ERR ", "red", "✖"),
        ["fail"] = new LogLevelConfig("FAIL", "red", "✖"),
        ["crit"] = new LogLevelConfig("CRIT", "fuchsia", "‼"),
        ["fatal"] = new LogLevelConfig("FATAL", "fuchsia", "‼"),
        ["trace"] = new LogLevelConfig("TRCE", "grey69", ""),
        ["debug"] = new LogLevelConfig("DBG ", "grey", ""),
        ["information"] = new LogLevelConfig("INFO", "cornflowerblue", ""),
        ["warning"] = new LogLevelConfig("WARN", "yellow", "⚠"),
        ["critical"] = new LogLevelConfig("CRIT", "fuchsia", "‼"),
        ["none"] = new LogLevelConfig("NONE", "white", "")
    };

    /// <summary>
    /// Parses a log line into structured components.
    /// </summary>
    internal static LogEntry Parse(string line, bool parseJson)
    {
        var entry = new LogEntry { Raw = line };

        if (parseJson && line.TrimStart().StartsWith("{"))
        {
            try
            {
                var json = JsonDocument.Parse(line);
                var root = json.RootElement;

                string[] levelProps = { "LogLevel", "Level", "level", "Severity", "severity" };
                foreach (var prop in levelProps)
                {
                    if (root.TryGetProperty(prop, out var levelElement))
                    {
                        entry.Level = levelElement.GetString() ?? entry.Level;
                        break;
                    }
                }

                string[] timestampProps = { "Timestamp", "timestamp", "@timestamp", "Time", "time", "Date" };
                foreach (var prop in timestampProps)
                {
                    if (root.TryGetProperty(prop, out var timeElement))
                    {
                        if (timeElement.ValueKind == JsonValueKind.String)
                        {
                            if (DateTime.TryParse(timeElement.GetString(), out var dt))
                            {
                                entry.Timestamp = dt;
                                break;
                            }
                        }
                    }
                }

                string[] messageProps = { "Message", "message", "msg", "Text" };
                foreach (var prop in messageProps)
                {
                    if (root.TryGetProperty(prop, out var msgElement))
                    {
                        entry.Message = msgElement.ToString();
                        break;
                    }
                }

                if (root.TryGetProperty("Exception", out var excElement) ||
                    root.TryGetProperty("exception", out excElement))
                {
                    entry.HasException = true;
                }

                entry.Properties = ExtractProperties(root);
                entry.IsStructured = true;
                return entry;
            }
            catch
            {
                // Not valid JSON, continue with text parsing
            }
        }

        var timestampMatch = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?|\d{2}:\d{2}:\d{2}(?:\.\d+)?)");
        if (timestampMatch.Success && DateTime.TryParse(timestampMatch.Groups[1].Value, out var ts))
        {
            entry.Timestamp = ts;
        }

        var levelMatch = Regex.Match(line, @"\[(trce|dbug|info|warn|error|fail|crit|fatal|trace|debug|information|warning|critical)\]", RegexOptions.IgnoreCase);
        if (!levelMatch.Success)
        {
            levelMatch = Regex.Match(line, @"\b(TRCE|DBG|INFO|WARN|ERR|FAIL|CRIT|FATAL)\b");
        }
        if (levelMatch.Success)
        {
            entry.Level = levelMatch.Groups[1].Value.ToLower();
        }

        var messageMatch = Regex.Match(line, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?\s+\[[^\]]+\]\s+(.*)");
        if (!messageMatch.Success)
        {
            messageMatch = Regex.Match(line, @"^\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+\S+\s+\[[^\]]+\]\s+(.*)");
        }
        entry.Message = messageMatch.Success ? messageMatch.Groups[1].Value : line;

        return entry;
    }

    /// <summary>
    /// Extracts property values from JSON for highlighting, excluding known standard fields.
    /// </summary>
    private static Dictionary<string, string> ExtractProperties(JsonElement root)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var standardProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LogLevel", "Level", "level", "Severity", "severity",
            "Timestamp", "timestamp", "@timestamp", "Time", "time", "Date",
            "Message", "message", "msg", "Text",
            "Exception", "exception",
            "EventId", "eventId", "EventName", "eventName",
            "SourceContext", "sourcecontext", "SourceContext",
            "Action", "action", "ActionId",
            "State", "state", "scopes",
            "ThreadId", "threadid", "Thread",
            "ProcessId", "processid",
            "MachineName", "machine",
            "Environment", "env", "EnvironmentName"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (standardProps.Contains(property.Name))
                continue;

            string value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                JsonValueKind.Array => $"[{property.Value.GetArrayLength()} items]",
                JsonValueKind.Object => "{...}",
                _ => property.Value.ToString()
            };

            if (value.Length > 100)
                continue;

            properties[property.Name] = value;
        }

        return properties;
    }

    /// <summary>
    /// Gets the log level configuration for a given level string.
    /// </summary>
    internal static LogLevelConfig GetLogLevelConfig(string? level)
    {
        if (string.IsNullOrEmpty(level))
            return LogLevels["none"];

        return LogLevels.TryGetValue(level.ToLowerInvariant(), out var config)
            ? config
            : LogLevels["none"];
    }
}
