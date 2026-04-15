# Section 04 — Code Review Interview

**Date**: 2026-04-11  
**Section**: section-04-trace-infrastructure

---

## Interview Questions and Decisions

### Q1 — H-1 + H-2: Path traversal (Security)
**Question**: `callId` from `FunctionResultContent` is used directly as a filename. `TaskId` from `RunMetadata` is used directly in `TraceScope.ResolveDirectory`. Both are caller/LLM-provided strings with no sanitization. Fix both, fix H-1 only, or defer?  
**User answer**: A — Fix both  
**Decision**: Fixed

---

### Q2 — H-3: SemaphoreSlim never disposed
**Question**: Each `StartRunAsync` creates a `FileSystemTraceWriter` with a `SemaphoreSlim(1,1)` that's never disposed. Dispose in `CompleteAsync` (simple) or add `IAsyncDisposable` to `ITraceWriter` (more correct)?  
**User answer**: B — Add `IAsyncDisposable` to `ITraceWriter` interface  
**Decision**: Fixed

---

### Q3 — M-4: InputHash hashes result not input
**Question**: `InputHash` / `tool.input_hash` constant hashes `ToolConventions.ToolCallResult` (tool output) but name says "input". Rename to `OutputHash`, or hash actual tool arguments?  
**User answer**: B — Hash the actual tool input (arguments tag)  
**Decision**: Fixed — added `ToolCallArguments = "gen_ai.tool.call.arguments"` constant, updated processor to hash from that tag, updated doc comment, updated test

---

## Auto-fixes Applied

| Finding | Action |
|---------|--------|
| H-4: Dead fields `_agentName`, `_startedAt` | Removed from `FileSystemTraceWriter` |
| M-2 + L-1: Duplicate `WriteAtomicAsync` and `JsonOptions` in nested class | Removed from nested class; uses outer class members |
| M-3: `CompleteAsync` serializes without `JsonOptions` | Passed `JsonOptions` to `JsonSerializer.Serialize` |
| M-5: No trace support in `GetStreamingResponseAsync` | Added `AppendFunctionResultTracesAsync` call before streaming |
| M-6: Negative `turnNumber` not validated | Added `ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turnNumber)` |
| L-4: `IOptions` instead of `IOptionsMonitor` | Switched to `IOptionsMonitor<AppConfig>` |
| N-1: Magic string `"__traceWriter"` | Defined `ITraceWriter.AdditionalPropertiesKey = "__traceWriter"` as a public const on the interface |

## Findings Let Go

| Finding | Reason |
|---------|--------|
| L-2: Iteration baggage not set | No `Iteration` property on `TraceScope` yet — belongs in a later section when the eval loop is implemented |
| L-3: Test parses JSONL lines twice | Test-efficiency nitpick; not worth the noise |
| N-2: Manifest uses anon type with manual snake_case | POC acceptable; no named record needed for a 4-field manifest |
