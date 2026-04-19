using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Presentation.LoggerUI;

/// <summary>
/// A generic log viewer that connects to a named pipe server
/// and displays colored, formatted log output.
/// </summary>
/// <remarks>
/// <para>
/// This log viewer is framework-agnostic and works with any logging system
/// that writes to a named pipe. It supports multiple log formats including:
/// </para>
/// <list type="bullet">
///   <item>Plain text logs with log level prefixes</item>
///   <item>Structured JSON logs (Serilog, Microsoft.Extensions.Logging)</item>
///   <item>Custom timestamp formats</item>
/// </list>
/// <para><b>Usage:</b></para>
/// <code>
/// // Run with default pipe name (AgenticHarnessLogs)
/// dotnet run --project Presentation.LoggerUI
///
/// // Run with custom pipe name
/// dotnet run --project Presentation.LoggerUI MyCustomLogs
/// </code>
/// <para><b>Keyboard Shortcuts:</b></para>
/// <list type="bullet">
///   <item><c>C</c> - Clear log screen</item>
///   <item><c>Ctrl+C</c> - Exit</item>
/// </list>
/// </remarks>
public class Program
{
	private static readonly string[] DefaultPipeNames =
	{
		"AgenticHarnessLogs.ConsoleUI",
		"AgenticHarnessLogs.AgentHub"
	};

	// Palette cycled per pipe index so each source tag gets a stable color.
	private static readonly string[] SourceColors =
	{
		"deepskyblue1",
		"lightgoldenrod1",
		"palegreen1",
		"lightpink1",
		"plum2"
	};

	// Serializes writes from multiple reader tasks so log lines don't interleave.
	private static readonly object _outputLock = new();

	// Log level configurations with colors and icons
	private static readonly Dictionary<string, LogLevelConfig> LogLevels = new(StringComparer.OrdinalIgnoreCase)
	{
		["trce"] = new LogLevelConfig("TRCE", "grey69", ""),
		["dbug"] = new LogLevelConfig("DBG ", "grey", ""),
		["info"] = new LogLevelConfig("INFO", "cornflowerblue", ""),
		["warn"] = new LogLevelConfig("WARN", "yellow", "\u26a0"),
		["error"] = new LogLevelConfig("ERR ", "red", "\u2716"),
		["fail"] = new LogLevelConfig("FAIL", "red", "\u2716"),
		["crit"] = new LogLevelConfig("CRIT", "fuchsia", "\u203c"),
		["fatal"] = new LogLevelConfig("FATAL", "fuchsia", "\u203c"),
		// Microsoft.Extensions.Logging levels
		["trace"] = new LogLevelConfig("TRCE", "grey69", ""),
		["debug"] = new LogLevelConfig("DBG ", "grey", ""),
		["information"] = new LogLevelConfig("INFO", "cornflowerblue", ""),
		["warning"] = new LogLevelConfig("WARN", "yellow", "\u26a0"),
		["critical"] = new LogLevelConfig("CRIT", "fuchsia", "\u203c"),
		// None/Unknown
		["none"] = new LogLevelConfig("NONE", "white", "")
	};

	// Connection state
	private static DateTime _sessionStart = DateTime.UtcNow;

	// Options
	private static bool _parseJson = true;

	public static async Task Main(string[] args)
	{
		var options = ParseArguments(args);
		_parseJson = options.ParseJson;

		var title = options.PipeNames.Count == 1
			? $"Log Viewer - {options.PipeNames[0]}"
			: $"Log Viewer - {options.PipeNames.Count} sources";

		try { Console.Title = title; } catch { }
		try { Console.Clear(); } catch { /* non-interactive console */ }
		try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

		// Start keyboard listener for hotkeys
		var cts = new CancellationTokenSource();
		var keyListenerTask = Task.Run(() => KeyboardListener(options.PipeNames, cts));

		// Print header
		PrintHeader(options.PipeNames);

		// One reader task per pipe; each reconnects independently.
		var readerTasks = options.PipeNames
			.Select((name, idx) => RunLogViewerAsync(name, BuildSourceTag(name, idx), cts))
			.ToArray();

		await Task.WhenAll(readerTasks);

		// Cleanup
		cts.Cancel();
		try { await keyListenerTask; }
		catch { /* Expected when cancelled */ }
	}

	/// <summary>
	/// Builds the per-source tag (e.g. "[ConsoleUI]") with a stable color from the palette.
	/// </summary>
	private static string BuildSourceTag(string pipeName, int index)
	{
		// Use everything after the last '.' as the short tag; fall back to full name.
		var dot = pipeName.LastIndexOf('.');
		var label = (dot >= 0 && dot < pipeName.Length - 1) ? pipeName.Substring(dot + 1) : pipeName;
		var color = SourceColors[index % SourceColors.Length];
		return $"[{color}][[{label}]][/]";
	}

	/// <summary>
	/// Reader loop for a single named pipe; reconnects on disconnect.
	/// </summary>
	private static async Task RunLogViewerAsync(string pipeName, string sourceTag, CancellationTokenSource cts)
	{
		while (!cts.IsCancellationRequested)
		{
			try
			{
				await using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.In);

				WriteStatusLine($"{sourceTag} [yellow]Waiting for log server ({pipeName})...[/]");
				await pipeClient.ConnectAsync(cts.Token);
				WriteStatusLine($"{sourceTag} [green]Connected![/]");

				_sessionStart = DateTime.UtcNow;

				using var reader = new StreamReader(pipeClient);

				string? line;
				while ((line = await reader.ReadLineAsync(cts.Token)) != null && !cts.IsCancellationRequested)
				{
					ProcessLogLine(line, sourceTag);
				}

				WriteStatusLine($"{sourceTag} [yellow]Server disconnected. Reconnecting...[/]");
			}
			catch (OperationCanceledException) { break; }
			catch (IOException)
			{
				WriteStatusLine($"{sourceTag} [yellow]Connection lost. Reconnecting...[/]");
			}
			catch (Exception ex)
			{
				WriteStatusLine($"{sourceTag} [red]Error: {ex.Message}[/]");
			}

			try { await Task.Delay(500, cts.Token); }
			catch (OperationCanceledException) { break; }
		}
	}

	/// <summary>
	/// Processes and displays a single log line tagged with its source.
	/// </summary>
	private static void ProcessLogLine(string line, string sourceTag)
	{
		if (string.IsNullOrWhiteSpace(line))
			return;

		try
		{
			var logEntry = ParseLogEntry(line);
			DisplayLogEntry(logEntry, sourceTag);
		}
		catch
		{
			// Display failure (malformed markup, unprintable chars, etc.) must never
			// kill the read loop — fall back to a safe raw write.
			lock (_outputLock)
			{
				try { Console.WriteLine(line); }
				catch { /* swallow — last-ditch fallback */ }
			}
		}
	}

	/// <summary>
	/// Parses a log line into structured components.
	/// </summary>
	private static LogEntry ParseLogEntry(string line)
	{
		var entry = new LogEntry { Raw = line };

		// Try to parse as JSON first (structured logging)
		if (_parseJson && line.TrimStart().StartsWith("{"))
		{
			try
			{
				var json = JsonDocument.Parse(line);
				var root = json.RootElement;

				// Try common property names for log level
				string[] levelProps = { "LogLevel", "Level", "level", "Severity", "severity" };
				foreach (var prop in levelProps)
				{
					if (root.TryGetProperty(prop, out var levelElement))
					{
						entry.Level = levelElement.GetString() ?? entry.Level;
						break;
					}
				}

				// Try common property names for timestamp
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

				// Try common property names for message
				string[] messageProps = { "Message", "message", "msg", "Text" };
				foreach (var prop in messageProps)
				{
					if (root.TryGetProperty(prop, out var msgElement))
					{
						entry.Message = msgElement.ToString();
						break;
					}
				}

				// Check for exception
				if (root.TryGetProperty("Exception", out var excElement) ||
				    root.TryGetProperty("exception", out excElement))
				{
					entry.HasException = true;
				}

				// Extract property values for highlighting
				entry.Properties = ExtractProperties(root);

				entry.IsStructured = true;
				return entry;
			}
			catch
			{
				// Not valid JSON, continue with text parsing
			}
		}

		// Parse text format
		// Format: "timestamp [level] message"
		// Example: "2026-01-14 10:00:00 [info] Loading context for phase 0..."

		// Extract timestamp — supports ISO date-time and HH:mm:ss[.fff] pipe format
		var timestampMatch = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?|\d{2}:\d{2}:\d{2}(?:\.\d+)?)");
		if (timestampMatch.Success && DateTime.TryParse(timestampMatch.Groups[1].Value, out var ts))
		{
			entry.Timestamp = ts;
		}

		// Extract log level — accepts [level], bare token after timestamp (INFO/WARN/DBG/TRCE/ERR/FAIL/CRIT),
		// or full names from structured formatters.
		var levelMatch = Regex.Match(line, @"\[(trce|dbug|info|warn|error|fail|crit|fatal|trace|debug|information|warning|critical)\]", RegexOptions.IgnoreCase);
		if (!levelMatch.Success)
		{
			levelMatch = Regex.Match(line, @"\b(TRCE|DBG|INFO|WARN|ERR|FAIL|CRIT|FATAL)\b");
		}
		if (levelMatch.Success)
		{
			entry.Level = levelMatch.Groups[1].Value.ToLower();
		}

		// Extract message content after timestamp and level marker
		var messageMatch = Regex.Match(line, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?\s+\[[^\]]+\]\s+(.*)");
		if (!messageMatch.Success)
		{
			// Pipe format: "HH:mm:ss.fff LEVEL [Category] message"
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

		// Standard property names to exclude (non-variable fields)
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

			// Get string representation of the value
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

			// Skip very long values (likely full messages or stack traces)
			if (value.Length > 100)
				continue;

			properties[property.Name] = value;
		}

		return properties;
	}

	/// <summary>
	/// Displays a log entry with formatting, colors, and a source tag.
	/// </summary>
	private static void DisplayLogEntry(LogEntry entry, string sourceTag)
	{
		var config = GetLogLevelConfig(entry.Level);
		var color = config.Color;
		var icon = config.Icon;
		var displayLevel = config.Name;

		// Create timestamp display
		var timestamp = entry.Timestamp.HasValue
			? $"[grey]{entry.Timestamp.Value:HH:mm:ss}[/]"
			: "[grey]         [/]";

		// Create level badge
		var levelBadge = $"[{color}]{displayLevel}[/]";

		// Create icon if present
		var iconPart = !string.IsNullOrEmpty(icon) ? $"[{color}]{icon}[/] " : "";

		// Format the message with variable highlighting
		var message = FormatMessageWithHighlight(entry);

		// Special handling for errors with exceptions
		if (entry.HasException)
		{
			iconPart = $"[{color}]\u2716[/] ";
		}

		// Lock so concurrent reader tasks don't interleave a line with an exception panel.
		lock (_outputLock)
		{
			AnsiConsole.MarkupLine($"{timestamp} {sourceTag} {levelBadge} {iconPart}{message}");

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
	}

	/// <summary>
	/// Formats a log message with variable values highlighted.
	/// For structured logs, highlights property values found in the message.
	/// For text logs, applies generic highlighting patterns.
	/// </summary>
	private static string FormatMessageWithHighlight(LogEntry entry)
	{
		var message = entry.Message;

		// For structured logs, highlight property values
		if (entry.IsStructured && entry.Properties.Count > 0)
		{
			// Try to find a message template in the JSON
			string? messageTemplate = null;
			if (entry.IsStructured)
			{
				try
				{
					var json = JsonDocument.Parse(entry.Raw);
					var root = json.RootElement;

					// Check for template fields (Serilog, MEL, etc.)
					string[] templateProps = { "MessageTemplate", "messageTemplate", "@t", "template" };
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

			// If we have a template, replace placeholders with highlighted values
			if (!string.IsNullOrEmpty(messageTemplate))
			{
				return FormatTemplateWithHighlight(messageTemplate, entry.Properties);
			}

			// Otherwise, find and highlight property values in the rendered message
			return FormatMessageWithValueHighlighting(message, entry.Properties);
		}

		// For text logs, apply generic highlighting patterns
		return HighlightGenericPatterns(message);
	}

	/// <summary>
	/// Formats a message template by replacing {placeholders} with highlighted property values.
	/// </summary>
	private static string FormatTemplateWithHighlight(string template, Dictionary<string, string> properties)
	{
		var result = new StringBuilder();
		var currentIndex = 0;

		// Match {PropertyName} placeholders
		var matches = Regex.Matches(template, @"\{(\w+)\}");

		foreach (Match match in matches.Cast<Match>().OrderBy(m => m.Index))
		{
			// Append text before the placeholder
			result.Append(EscapeMarkup(template.Substring(currentIndex, match.Index - currentIndex)));

			var propName = match.Groups[1].Value;

			// Try to find the property value (case-insensitive)
			var propValue = properties.FirstOrDefault(p =>
				string.Equals(p.Key, propName, StringComparison.OrdinalIgnoreCase)).Value;

			if (!string.IsNullOrEmpty(propValue))
			{
				// Append highlighted value
				result.Append($"[bold yellow on darkblue]{EscapeMarkup(propValue)}[/]");
			}
			else
			{
				// Property not found, keep the placeholder
				result.Append(EscapeMarkup(match.Value));
			}

			currentIndex = match.Index + match.Length;
		}

		// Append remaining text after last placeholder
		if (currentIndex < template.Length)
		{
			result.Append(EscapeMarkup(template.Substring(currentIndex)));
		}

		return result.ToString();
	}

	/// <summary>
	/// Formats a message by finding and highlighting all occurrences of property values.
	/// Uses a regex-based approach to find all matches without forward-only limitations.
	/// </summary>
	private static string FormatMessageWithValueHighlighting(string message, Dictionary<string, string> properties)
	{
		var result = new StringBuilder();
		var lastIndex = 0;

		// Find all matches of all property values in the message
		var allMatches = new List<(int Index, string Value)>();

		foreach (var (propName, value) in properties)
		{
			if (string.IsNullOrEmpty(value) || value.Length < 2)
				continue;

			// Find all occurrences of this value in the message
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

		// Sort matches by position and remove overlaps (longer values first)
		var sortedMatches = allMatches
			.OrderByDescending(m => m.Value.Length)
			.ThenBy(m => m.Index)
			.ToList();

		// Filter out overlapping matches
		var nonOverlapping = new List<(int Index, int Length, string Value)>();
		foreach (var (index, value) in sortedMatches)
		{
			if (nonOverlapping.Any(m => index < m.Index + m.Length && index + value.Length > m.Index))
				continue;
			nonOverlapping.Add((index, value.Length, value));
		}

		// Sort by position for output
		nonOverlapping = nonOverlapping.OrderBy(m => m.Index).ToList();

		// Build the result
		foreach (var (index, length, value) in nonOverlapping)
		{
			// Append text before the match
			result.Append(EscapeMarkup(message.Substring(lastIndex, index - lastIndex)));

			// Append highlighted value
			var actualValue = message.Substring(index, length);
			result.Append($"[bold yellow on darkblue]{EscapeMarkup(actualValue)}[/]");

			lastIndex = index + length;
		}

		// Append remaining text
		if (lastIndex < message.Length)
		{
			result.Append(EscapeMarkup(message.Substring(lastIndex)));
		}

		return result.ToString();
	}

	/// <summary>
	/// Applies generic highlighting patterns for non-structured logs.
	/// Detects and highlights common value patterns like quoted strings, numbers, paths, etc.
	/// </summary>
	private static string HighlightGenericPatterns(string message)
	{
		var result = EscapeMarkup(message);

		// Highlight quoted strings
		result = Regex.Replace(
			result,
			@"&quot;([^&]*)&quot;",
			"[bold yellow on darkblue]&quot;$1[/][bold yellow on darkblue]&quot;[/]");

		// Highlight file paths (Windows and Unix)
		result = Regex.Replace(
			result,
			@"([A-Z]:\\[^[\]]+|/[^[\]\s]+)",
			"[bold yellow on darkblue]$1[/]",
			RegexOptions.IgnoreCase);

		// Highlight numbers (but not log levels or timestamps)
		result = Regex.Replace(
			result,
			@"\b(\d{4,})\b(?!\])",  // 4+ digits (likely IDs)
			"[bold cyan]$1[/]");

		return result;
	}

	/// <summary>
	/// Gets the log level configuration for a given level string.
	/// </summary>
	private static LogLevelConfig GetLogLevelConfig(string? level)
	{
		if (string.IsNullOrEmpty(level))
			return LogLevels["none"];

		return LogLevels.TryGetValue(level.ToLowerInvariant(), out var config)
			? config
			: LogLevels["none"];
	}

	/// <summary>
	/// Escapes Spectre.Console markup characters in text.
	/// </summary>
	private static string EscapeMarkup(string text)
	{
		return text.Replace("[", "[[").Replace("]", "]]");
	}

	/// <summary>
	/// Writes a status message as a regular output line (multi-pipe mode can't share a status row).
	/// </summary>
	private static void WriteStatusLine(string message)
	{
		lock (_outputLock)
		{
			try { AnsiConsole.MarkupLine($"\u25cf {message}"); }
			catch { /* Ignore console errors */ }
		}
	}

	/// <summary>
	/// Prints the initial header banner listing all monitored pipes.
	/// </summary>
	private static void PrintHeader(IReadOnlyList<string> pipeNames)
	{
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
		AnsiConsole.MarkupLine("[grey]Press C to clear, Ctrl+C to exit[/]");
		AnsiConsole.WriteLine();
	}

	/// <summary>
	/// Listens for keyboard input.
	/// </summary>
	private static void KeyboardListener(IReadOnlyList<string> pipeNames, CancellationTokenSource cts)
	{
		while (!cts.IsCancellationRequested)
		{
			ConsoleKeyInfo key;
			try { key = Console.ReadKey(true); }
			catch { return; /* no console / redirected — keyboard listener disabled */ }
			if (key.Key == ConsoleKey.C)
			{
				Console.Clear();
				PrintHeader(pipeNames);
			}
			else if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C)
			{
				cts.Cancel();
				break;
			}
		}
	}

	/// <summary>
	/// Parses command line arguments. Collects one or more pipe names from positional args
	/// or repeated --pipe flags; defaults to <see cref="DefaultPipeNames"/> if none supplied.
	/// </summary>
	private static LoggerOptions ParseArguments(string[] args)
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

	/// <summary>
	/// Configuration for a log level including display name, color, and icon.
	/// </summary>
	private record LogLevelConfig(string Name, string Color, string Icon);

	/// <summary>
	/// Represents a parsed log entry.
	/// </summary>
	private class LogEntry
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
	private class LoggerOptions
	{
		public List<string> PipeNames { get; set; } = new();
		public bool ParseJson { get; set; } = true;
	}
}
