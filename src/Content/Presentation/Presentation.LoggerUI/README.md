# Presentation.LoggerUI

A standalone real-time log viewer that connects to the Agentic Harness via named pipes and renders a live stream of structured log output with color-coded levels, template variable highlighting, and formatted exception panels. When the harness is running an agent conversation with tool calls and sub-agent spawns, this is where you see the full debug picture -- every log line, every structured property, every stack trace.

When you run it, you see a colored terminal with log entries streaming in real time. Each source (ConsoleUI, AgentHub) gets its own color tag. Log levels have distinct badges and colors. Property values from structured logs are highlighted with yellow-on-blue backgrounds so they stand out from static text.

## Architecture Context

```
┌────────────────────────┐         Named Pipe          ┌──────────────────────┐
│  Presentation.AgentHub │ ──── "AgenticHarnessLogs ─→ │  Presentation        │
│  (NamedPipeLogger)     │       .AgentHub"            │  .LoggerUI           │
└────────────────────────┘                             │                      │
                                                       │  Parses JSON/text    │
┌────────────────────────┐         Named Pipe          │  Color-codes levels  │
│  Presentation.ConsoleUI│ ──── "AgenticHarnessLogs ─→ │  Highlights values   │
│  (NamedPipeLogger)     │       .ConsoleUI"           │  Formats exceptions  │
└────────────────────────┘                             └──────────────────────┘
```

**Key design decision:** LoggerUI has ZERO project references. It speaks the named pipe protocol and nothing else. It can monitor any application that writes structured logs to a named pipe, not just the Agentic Harness.

## Key Concepts

### Multi-Source Monitoring

LoggerUI connects to multiple named pipes simultaneously, each in its own async reader task. By default, it monitors both:
- `AgenticHarnessLogs.ConsoleUI` -- logs from the terminal app
- `AgenticHarnessLogs.AgentHub` -- logs from the web API

Each source gets a color-coded tag (e.g., `[ConsoleUI]` in blue, `[AgentHub]` in gold) so you can instantly identify which process generated each line. A write lock prevents concurrent reader tasks from interleaving output.

### Dual-Format Log Parsing

LoggerUI transparently handles two formats:

**Structured JSON** (detected by leading `{`): Extracts `LogLevel`/`Level`/`Severity`, `Timestamp`/`@timestamp`/`Time`, `Message`/`msg`, and `Exception` from the JSON. Property names are normalized across SDK variations (Serilog, Microsoft.Extensions.Logging, NLog).

**Plain Text** (regex-based): Parses `timestamp [level] message` format, supporting ISO 8601 datetime and `HH:mm:ss.fff` pipe formats.

### Property Value Highlighting

When structured logs contain property values, LoggerUI finds them in the rendered message and highlights them with `[bold yellow on darkblue]` markup. Two highlighting strategies:

1. **Template-based**: If the JSON contains a `MessageTemplate` field, `{Placeholder}` tokens are replaced with highlighted property values.
2. **Value-search**: If no template is available, all property values are found in the message text and highlighted in place.

Generic patterns (quoted strings, file paths, large numbers) are also highlighted in plain-text logs.

### Visual Formatting

| Level | Badge | Color | Icon |
|-------|-------|-------|------|
| Trace | `TRCE` | Grey | (none) |
| Debug | `DBG ` | Grey | (none) |
| Information | `INFO` | Cornflower Blue | (none) |
| Warning | `WARN` | Yellow | (warning sign) |
| Error | `ERR ` | Red | (cross mark) |
| Critical | `CRIT` | Fuchsia | (double exclamation) |
| Fatal | `FATAL` | Fuchsia | (double exclamation) |

Exception stack traces are rendered in red-bordered panels with a `[red]Exception[/]` header. Fallback: if Spectre markup fails (malformed characters), raw console output is used.

### Resilient Connection

- If the harness isn't running, LoggerUI waits for the pipe server to appear
- If the connection drops, it auto-reconnects with 500ms backoff
- You can start LoggerUI first and the harness second
- `Ctrl+C` triggers graceful shutdown via `CancellationTokenSource`

## Project Structure

```
Presentation.LoggerUI/
└── Program.cs          The entire application (single-file design)
                        ├── Main()                    Entry point, pipe name parsing
                        ├── RunLogViewerAsync()       Per-pipe reader loop with reconnect
                        ├── ProcessLogLine()          Parse + display (exception-safe)
                        ├── ParseLogEntry()           JSON/text format detection + parsing
                        ├── DisplayLogEntry()         Spectre.Console rendering
                        ├── FormatMessageWithHighlight()  Property value highlighting
                        ├── FormatTemplateWithHighlight() Template placeholder replacement
                        ├── HighlightGenericPatterns()    Path/number/string detection
                        ├── KeyboardListener()        C=clear, Ctrl+C=exit
                        └── PrintHeader()             Banner with source list
```

This is intentionally a single-file application. No layers, no abstractions, no DI -- just a focused utility.

## Configuration

No appsettings.json required. All configuration is via command-line arguments:

| Argument | Purpose | Default |
|----------|---------|---------|
| (positional) | Pipe name(s) to monitor | `AgenticHarnessLogs.ConsoleUI`, `AgenticHarnessLogs.AgentHub` |
| `--pipe` / `-p` | Named pipe (repeatable) | (same as positional) |
| `--no-json` | Disable JSON parsing | `false` (JSON parsing enabled) |

The pipe name must match the `AppConfig.Logging.PipeName` setting in the source application's configuration.

## How to Run

```bash
# Default (monitors both ConsoleUI and AgentHub pipes)
dotnet run --project src/Content/Presentation/Presentation.LoggerUI

# Monitor only AgentHub
dotnet run --project src/Content/Presentation/Presentation.LoggerUI -- AgenticHarnessLogs.AgentHub

# Monitor a custom pipe
dotnet run --project src/Content/Presentation/Presentation.LoggerUI -- --pipe MyApp.Logs

# Multiple custom pipes
dotnet run --project src/Content/Presentation/Presentation.LoggerUI -- -p Pipe1 -p Pipe2

# Disable JSON parsing (plain text mode)
dotnet run --project src/Content/Presentation/Presentation.LoggerUI -- --no-json
```

**Typical development workflow:**

Terminal 1: LoggerUI (waiting for pipes)
```bash
dotnet run --project src/Content/Presentation/Presentation.LoggerUI
```

Terminal 2: ConsoleUI or AgentHub (generates logs)
```bash
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example research
```

LoggerUI connects automatically when the harness starts and shows real-time structured output.

### Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `C` | Clear screen and reprint header |
| `Ctrl+C` | Graceful shutdown |

## Common Tasks

### Monitoring a Custom Application

Any .NET application can write to a named pipe that LoggerUI monitors. The pipe protocol is simple: one log entry per line, either as JSON or as formatted text. Configure the pipe name in the source app's logging pipeline and pass it to LoggerUI.

### Filtering by Source

With multiple pipes, each source gets a colored tag. Currently there's no built-in filtering, but you can limit to a single source by passing only that pipe name as an argument.

### Debugging LoggerUI Itself

If log lines appear garbled or unparsed, run with `--no-json` to see raw output without parsing. Check if the source is writing valid JSON or if there are encoding issues.

## Dependencies

**Project References:** None (completely standalone)

**NuGet Packages:**
- `Spectre.Console` -- rich terminal rendering (colors, panels, rules, markup)

That's it. LoggerUI is the most decoupled project in the entire solution -- it depends on nothing except Spectre.Console and the .NET runtime.

## Testing

**Test approach:** LoggerUI is a UI utility without automated tests. Verification is manual:

1. Start LoggerUI
2. Start a harness host (ConsoleUI or AgentHub)
3. Verify structured JSON logs render with correct colors, property highlighting, and exception panels
4. Verify plain text logs render with level detection and timestamp parsing
5. Verify reconnection after pipe disconnect
6. Verify `C` (clear) and `Ctrl+C` (exit) keyboard shortcuts

For automated testing of the named pipe protocol, see `Tests/Application.Common.Tests` which covers the `NamedPipeLogger` writer side.
