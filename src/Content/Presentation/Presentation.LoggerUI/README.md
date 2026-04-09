# Presentation.LoggerUI

When an agent is running a multi-turn conversation with tool calls, compaction events, and sub-agent spawns, the main ConsoleUI shows the high-level results. But sometimes you need to see *everything* — every log line, every structured property, every exception stack trace — in real time.

Presentation.LoggerUI is a standalone log viewer that connects to the harness over a named pipe and renders a live stream of structured log output with color-coded levels, property highlighting, and formatted exception panels.

---

## How It Works

The harness's `NamedPipeLogger` (defined in Application.Common) streams structured log entries over a named pipe. LoggerUI connects to that pipe, parses incoming lines, and renders them with [Spectre.Console](https://spectreconsole.net/) for rich terminal formatting.

The connection is resilient: if the harness isn't running, LoggerUI waits. If the connection drops mid-session, it auto-reconnects with a 500ms backoff. You can start LoggerUI first and the harness second — it'll connect as soon as the pipe is available.

## Dual-Format Parsing

LoggerUI handles two log formats transparently:

**Structured JSON** (Serilog, Microsoft.Extensions.Logging) — Detected by a leading `{`. The parser extracts log level, timestamp, message, exception, and custom properties from the JSON object. Property names are normalized across SDK variations (`LogLevel` vs `Level` vs `Severity`, `Timestamp` vs `@timestamp` vs `Time`).

**Plain Text** — Parsed via regex for the common `timestamp [level] message` format. ISO 8601 timestamps with optional timezone offsets are recognized.

Custom properties from structured logs get highlighted in the rendered output — when a log template uses `{PropertyName}`, the actual value is displayed with a distinctive yellow-on-dark-blue background so it stands out from the surrounding text.

## Visual Formatting

Each log level gets its own color and Unicode icon:

| Level | Badge | Color |
|-------|-------|-------|
| Trace | `TRCE` | Grey |
| Debug | `DBG` | Grey |
| Information | `INFO` | Cornflower Blue |
| Warning | `WARN ⚠` | Yellow |
| Error | `ERR ✖` | Red |
| Critical | `CRIT ‼` | Fuchsia |
| Fatal | `FAIL ✖` | Red |

Exception stack traces are wrapped in red-bordered panels for immediate visibility. Generic text patterns — quoted strings, file paths (Windows and Unix), large numbers — are also highlighted for readability.

## Usage

```bash
# Default — connects to pipe named "AgenticHarnessLogs"
dotnet run --project src/Content/Presentation/Presentation.LoggerUI

# Custom pipe name
dotnet run --project src/Content/Presentation/Presentation.LoggerUI -- --pipe MyCustomPipe

# Disable JSON parsing (treat all input as plain text)
dotnet run --project src/Content/Presentation/Presentation.LoggerUI -- --no-json
```

**Keyboard shortcuts:**
- `C` — Clear console and reprint header
- `Ctrl+C` — Graceful shutdown

---

## Project Structure

```
Presentation.LoggerUI/
└── Program.cs    The entire application — entry point, pipe connection,
                  log parsing, formatting, and keyboard handling (681 lines)
```

This is intentionally a single-file application. It has no domain dependencies, no Application layer references, no Infrastructure coupling. It's a standalone utility that speaks the named pipe protocol and nothing else.

## Dependencies

- **Spectre.Console** — Rich terminal formatting (colors, panels, markup)

That's it. No project references. LoggerUI is completely decoupled from the harness — it connects over the named pipe protocol and can monitor any application that writes structured logs to a named pipe.
