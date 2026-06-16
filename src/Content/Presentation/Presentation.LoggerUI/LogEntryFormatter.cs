using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Presentation.LoggerUI;

/// <summary>
/// Renders parsed <see cref="LogEntry"/> instances to the console
/// with Spectre.Console markup, color-coded log levels, and variable highlighting.
/// </summary>
internal static class LogEntryFormatter
{
    private static readonly string[] SourceColors =
    {
        "deepskyblue1",
        "lightgoldenrod1",
        "palegreen1",
        "lightpink1",
        "plum2"
    };

    private static readonly object OutputLock = new();

    /// <summary>Severity rank per level name; higher is more severe. Unknown levels rank as info.</summary>
    private static readonly Dictionary<string, int> LevelRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trce"] = 0, ["trace"] = 0,
        ["dbug"] = 1, ["dbg"] = 1, ["debug"] = 1,
        ["info"] = 2, ["information"] = 2,
        ["warn"] = 3, ["warning"] = 3,
        ["error"] = 4, ["err"] = 4, ["fail"] = 4,
        ["crit"] = 5, ["critical"] = 5, ["fatal"] = 5,
    };

    /// <summary>Display names for each minimum-level filter step, indexed by rank.</summary>
    private static readonly string[] FilterNames = { "ALL", "DEBUG+", "INFO+", "WARN+", "ERROR+", "CRIT only" };

    /// <summary>Maximum number of parsed entries retained for filter-toggle replay.</summary>
    private const int HistoryCapacity = 5000;

    /// <summary>
    /// Ring buffer of parsed entries used to redraw the scrollback when the filter changes.
    /// All access is gated by <see cref="OutputLock"/> so writes and replay can't interleave.
    /// </summary>
    private static readonly Queue<(LogEntry Entry, string SourceTag)> History = new();

    /// <summary>Cached pipe-name list so <see cref="Redraw"/> can reprint the header without plumbing args.</summary>
    private static IReadOnlyList<string> _pipeNames = Array.Empty<string>();

    /// <summary>
    /// Minimum severity rank to display; entries below it are suppressed. 0 shows everything.
    /// <c>volatile</c> because the keyboard listener thread mutates it while reader threads read it.
    /// </summary>
    private static volatile int _minLevelRank;

    /// <summary>
    /// Toggles the warning filter: jumps straight to WARN+ from any other state, or back to ALL
    /// when already at WARN+. Bound to the <c>W</c> key for one-press "show me the warnings".
    /// Redraws the scrollback so already-visible entries are re-evaluated against the new filter.
    /// </summary>
    internal static void ToggleWarningFilter()
    {
        _minLevelRank = _minLevelRank == 3 ? 0 : 3;
        Redraw();
    }

    /// <summary>
    /// Advances the minimum-level filter one step, wrapping back to ALL. Bound to <c>L</c>.
    /// Redraws the scrollback so already-visible entries are re-evaluated against the new filter.
    /// </summary>
    internal static void CycleLevelFilter()
    {
        _minLevelRank = (_minLevelRank + 1) % FilterNames.Length;
        Redraw();
    }

    /// <summary>
    /// Clears both the visible scrollback and the in-memory history buffer.
    /// Bound to the <c>C</c> key — subsequent filter toggles will only replay entries that arrive after the clear.
    /// </summary>
    internal static void HandleClearKey()
    {
        lock (OutputLock)
        {
            History.Clear();
            try { Console.Clear(); } catch { /* terminal may not support clear */ }
            PrintHeaderUnlocked(_pipeNames);
        }
    }

    private static bool PassesFilter(LogEntry entry)
    {
        if (_minLevelRank == 0)
            return true;

        var rank = entry.Level is not null && LevelRanks.TryGetValue(entry.Level, out var r) ? r : 2;
        return rank >= _minLevelRank;
    }

    /// <summary>
    /// Clears the screen, reprints the header + active-filter banner, and replays
    /// every retained history entry that passes the current filter. Holds
    /// <see cref="OutputLock"/> for the whole operation so live reader threads
    /// block until the redraw finishes — entries they emit afterwards are
    /// already in the history buffer and render at the tail in order.
    /// </summary>
    private static void Redraw()
    {
        lock (OutputLock)
        {
            try { Console.Clear(); } catch { /* terminal may not support clear */ }
            PrintHeaderUnlocked(_pipeNames);
            try { AnsiConsole.MarkupLine($"● [bold]Filter:[/] showing [cyan]{FilterNames[_minLevelRank]}[/]"); }
            catch { /* Ignore console errors */ }

            foreach (var (entry, tag) in History)
            {
                if (PassesFilter(entry))
                    RenderEntryUnlocked(entry, tag);
            }
        }
    }

    /// <summary>
    /// Builds the per-source tag (e.g. "[ConsoleUI]") with a stable color from the palette.
    /// </summary>
    internal static string BuildSourceTag(string pipeName, int index)
    {
        var dot = pipeName.LastIndexOf('.');
        var label = (dot >= 0 && dot < pipeName.Length - 1) ? pipeName.Substring(dot + 1) : pipeName;
        var color = SourceColors[index % SourceColors.Length];
        return $"[{color}][[{label}]][/]";
    }

    /// <summary>
    /// Processes and displays a single log line tagged with its source.
    /// Appends to the replay history buffer and renders under a single lock acquisition
    /// so a concurrent <see cref="Redraw"/> can't observe a half-appended state.
    /// </summary>
    internal static void ProcessLogLine(string line, string sourceTag, bool parseJson)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        LogEntry logEntry;
        try
        {
            logEntry = LogEntryParser.Parse(line, parseJson);
        }
        catch
        {
            lock (OutputLock)
            {
                try { Console.WriteLine(line); }
                catch { /* swallow -- last-ditch fallback */ }
            }
            return;
        }

        lock (OutputLock)
        {
            History.Enqueue((logEntry, sourceTag));
            while (History.Count > HistoryCapacity)
                History.Dequeue();

            if (PassesFilter(logEntry))
                RenderEntryUnlocked(logEntry, sourceTag);
        }
    }

    /// <summary>
    /// Writes a status message as a regular output line.
    /// </summary>
    internal static void WriteStatusLine(string message)
    {
        lock (OutputLock)
        {
            try { AnsiConsole.MarkupLine($"● {message}"); }
            catch { /* Ignore console errors */ }
        }
    }

    /// <summary>
    /// Prints the initial header banner listing all monitored pipes.
    /// Caches the pipe-name list so <see cref="Redraw"/> and <see cref="HandleClearKey"/>
    /// can reprint the header on demand without re-plumbing arguments.
    /// </summary>
    internal static void PrintHeader(IReadOnlyList<string> pipeNames)
    {
        lock (OutputLock)
        {
            PrintHeaderUnlocked(pipeNames);
        }
    }

    /// <summary>Header rendering body; assumes the caller already holds <see cref="OutputLock"/>.</summary>
    private static void PrintHeaderUnlocked(IReadOnlyList<string> pipeNames)
    {
        _pipeNames = pipeNames;

        var heading = pipeNames.Count == 1
            ? $"[bold cornflowerblue]Log Viewer[/] - [cyan]{pipeNames[0]}[/]"
            : $"[bold cornflowerblue]Log Viewer[/] - [cyan]{pipeNames.Count} sources[/]";

        var rule = new Rule(heading);
        rule.RuleStyle("grey");
        AnsiConsole.Write(rule);

        AnsiConsole.WriteLine();
        for (int i = 0; i < pipeNames.Count; i++)
        {
            var tag = BuildSourceTag(pipeNames[i], i);
            AnsiConsole.MarkupLine($"  {tag} [grey]{pipeNames[i]}[/]");
        }
        AnsiConsole.MarkupLine("[grey]Press C to clear, W to toggle warnings-only, L to cycle level filter, Ctrl+C to exit[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Renders a single entry to the console.
    /// Assumes the caller already holds <see cref="OutputLock"/> — used by both the
    /// live-append path in <see cref="ProcessLogLine"/> and the replay path in <see cref="Redraw"/>.
    /// The main-line markup is computed once and cached on <see cref="LogEntry.RenderedMarkup"/>
    /// so redraws skip regex highlighting and string interpolation; only the Spectre
    /// markup-to-ANSI step still runs per render (unavoidable without a custom <c>IAnsiConsole</c>).
    /// </summary>
    private static void RenderEntryUnlocked(LogEntry entry, string sourceTag)
    {
        entry.RenderedMarkup ??= BuildEntryMarkup(entry, sourceTag);

        try { AnsiConsole.MarkupLine(entry.RenderedMarkup); }
        catch { /* malformed markup -- swallow rather than crash the viewer */ }

        if (entry.IsStructured && entry.HasException)
        {
            try
            {
                var panel = new Panel(entry.Raw)
                    .BorderColor(Color.Red)
                    .Header("[red]Exception[/]")
                    .RoundedBorder()
                    .Collapse();
                AnsiConsole.Write(panel);
            }
            catch
            {
                AnsiConsole.MarkupLine($"[red]{EscapeMarkup(entry.Raw)}[/]");
            }
        }
    }

    /// <summary>
    /// Builds the full Spectre markup string for an entry's main display line.
    /// Pure function — same inputs always produce the same markup, which is why the
    /// result is safe to cache on the entry and reuse across redraws.
    /// </summary>
    private static string BuildEntryMarkup(LogEntry entry, string sourceTag)
    {
        var config = LogEntryParser.GetLogLevelConfig(entry.Level);
        var color = config.Color;
        var icon = config.Icon;
        var displayLevel = config.Name;

        var timestamp = entry.Timestamp.HasValue
            ? $"[grey]{entry.Timestamp.Value:HH:mm:ss}[/]"
            : "[grey]         [/]";

        var levelBadge = $"[{color}]{displayLevel}[/]";

        var iconPart = entry.HasException
            ? $"[{color}]✖[/] "
            : (!string.IsNullOrEmpty(icon) ? $"[{color}]{icon}[/] " : "");

        var message = FormatMessageWithHighlight(entry);

        return $"{timestamp} {sourceTag} {levelBadge} {iconPart}{message}";
    }

    /// <summary>
    /// Formats a log message with variable values highlighted.
    /// </summary>
    private static string FormatMessageWithHighlight(LogEntry entry)
    {
        var message = entry.Message;

        if (entry.IsStructured && entry.Properties.Count > 0)
        {
            string? messageTemplate = null;
            if (entry.IsStructured)
            {
                try
                {
                    var json = JsonDocument.Parse(entry.Raw);
                    var root = json.RootElement;

                    string[] templateProps = { "MessageTemplate", "messageTemplate", "@mt", "template" };
                    foreach (var prop in templateProps)
                    {
                        if (root.TryGetProperty(prop, out var templateElement) &&
                            templateElement.ValueKind == JsonValueKind.String)
                        {
                            messageTemplate = templateElement.GetString();
                            break;
                        }
                    }
                }
                catch { /* Ignore JSON parse errors */ }
            }

            if (!string.IsNullOrEmpty(messageTemplate))
            {
                return FormatTemplateWithHighlight(messageTemplate, entry.Properties);
            }

            return FormatMessageWithValueHighlighting(message, entry.Properties);
        }

        return HighlightGenericPatterns(message);
    }

    /// <summary>
    /// Formats a message template by replacing {placeholders} with highlighted property values.
    /// </summary>
    private static string FormatTemplateWithHighlight(string template, Dictionary<string, string> properties)
    {
        var result = new StringBuilder();
        var currentIndex = 0;

        var matches = Regex.Matches(template, @"\{(\w+)\}");

        foreach (Match match in matches.Cast<Match>().OrderBy(m => m.Index))
        {
            result.Append(EscapeMarkup(template.Substring(currentIndex, match.Index - currentIndex)));

            var propName = match.Groups[1].Value;

            var propValue = properties.FirstOrDefault(p =>
                string.Equals(p.Key, propName, StringComparison.OrdinalIgnoreCase)).Value;

            if (!string.IsNullOrEmpty(propValue))
            {
                result.Append($"[bold yellow on darkblue]{EscapeMarkup(propValue)}[/]");
            }
            else
            {
                result.Append(EscapeMarkup(match.Value));
            }

            currentIndex = match.Index + match.Length;
        }

        if (currentIndex < template.Length)
        {
            result.Append(EscapeMarkup(template.Substring(currentIndex)));
        }

        return result.ToString();
    }

    /// <summary>
    /// Formats a message by finding and highlighting all occurrences of property values.
    /// </summary>
    private static string FormatMessageWithValueHighlighting(string message, Dictionary<string, string> properties)
    {
        var result = new StringBuilder();
        var lastIndex = 0;

        var allMatches = new List<(int Index, string Value)>();

        foreach (var (propName, value) in properties)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2)
                continue;

            var index = message.IndexOf(value, 0, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                allMatches.Add((index, value));
                index = message.IndexOf(value, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (allMatches.Count == 0)
        {
            return EscapeMarkup(message);
        }

        var sortedMatches = allMatches
            .OrderByDescending(m => m.Value.Length)
            .ThenBy(m => m.Index)
            .ToList();

        var nonOverlapping = new List<(int Index, int Length, string Value)>();
        foreach (var (index, value) in sortedMatches)
        {
            if (nonOverlapping.Any(m => index < m.Index + m.Length && index + value.Length > m.Index))
                continue;
            nonOverlapping.Add((index, value.Length, value));
        }

        nonOverlapping = nonOverlapping.OrderBy(m => m.Index).ToList();

        foreach (var (index, length, value) in nonOverlapping)
        {
            result.Append(EscapeMarkup(message.Substring(lastIndex, index - lastIndex)));

            var actualValue = message.Substring(index, length);
            result.Append($"[bold yellow on darkblue]{EscapeMarkup(actualValue)}[/]");

            lastIndex = index + length;
        }

        if (lastIndex < message.Length)
        {
            result.Append(EscapeMarkup(message.Substring(lastIndex)));
        }

        return result.ToString();
    }

    /// <summary>
    /// Applies generic highlighting patterns for non-structured logs.
    /// </summary>
    private static string HighlightGenericPatterns(string message)
    {
        var result = EscapeMarkup(message);

        result = Regex.Replace(
            result,
            @"&quot;([^&]*)&quot;",
            "[bold yellow on darkblue]&quot;$1[/][bold yellow on darkblue]&quot;[/]");

        result = Regex.Replace(
            result,
            @"([A-Z]:\\[^[\]]+|/[^[\]\s]+)",
            "[bold yellow on darkblue]$1[/]",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"\b(\d{4,})\b(?!\])",
            "[bold cyan]$1[/]");

        return result;
    }

    /// <summary>
    /// Escapes Spectre.Console markup characters in text.
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
