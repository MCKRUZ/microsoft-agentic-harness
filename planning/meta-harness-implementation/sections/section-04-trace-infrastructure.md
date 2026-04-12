# Section 04: Trace Infrastructure

## Overview

This section implements the core persistence layer for execution traces: the interfaces that the rest of the system depends on (`ITraceWriter`, `IExecutionTraceStore`), their filesystem implementation (`FileSystemExecutionTraceStore`), and the wiring into `AgentExecutionContextFactory` and `ToolDiagnosticsMiddleware`. This section also extends `ToolConventions` with new causal attribution span tags, and adds a new `CausalSpanAttributionProcessor` to the existing observability processor pipeline.

**Dependencies (must be complete before starting):**
- section-01-config: `MetaHarnessConfig` and `AppConfig.MetaHarness` binding
- section-02-secret-redaction: `ISecretRedactor` interface and `PatternSecretRedactor`
- section-03-trace-domain: `TraceScope`, `RunMetadata`, `TurnArtifacts`, `ExecutionTraceRecord`, `HarnessScores` value objects

**Blocks:** sections 05, 06, 10, 11, 12, 13 all depend on `ITraceWriter` and `IExecutionTraceStore` being available.

---

## Tests First

**Test project:** `Infrastructure.Observability.Tests` for span processor tests; `Infrastructure.AI.Tests` for trace store tests.

Note: the TDD plan splits these across two test files. The trace store tests belong in a new file; the span processor tests extend the existing `Infrastructure.Observability.Tests` project alongside `ToolEffectivenessProcessorTests`.

### Span Processor Tests

**File:** `src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs`

The existing `ToolEffectivenessProcessorTests` is the structural template — use the same `ActivitySource` + `ActivityListener` pattern. Tests must cover:

- `OnEnd_ToolCallSpan_AddsToolNameTag` — span with `gen_ai.operation.name = "execute_tool"` gets `gen_ai.tool.name` set from the existing `agent.tool.name` tag
- `OnEnd_ToolCallSpan_AddsInputHashTag` — when `ActivitySamplingResult.AllDataAndRecorded`, `tool.input_hash` is a non-empty SHA256 hex string
- `OnEnd_ToolCallSpan_AddsResultCategoryTag` — `tool.result_category` is set to one of the bucketed values (`success`, `partial`, `error`, `timeout`, `blocked`)
- `OnEnd_WhenCandidateIdOnContext_AddsCandidateIdTag` — span with `gen_ai.harness.candidate_id` tag already set passes through; processor reading it from Activity baggage adds it if present
- `OnEnd_WhenNoCandidateId_DoesNotAddCandidateIdTag` — span without candidate context gets no `gen_ai.harness.candidate_id` tag
- `OnEnd_InputHashComputation_IsNotPerformedWhenIsAllDataRequestedFalse` — when `Activity.IsAllDataRequested == false`, `tool.input_hash` tag is absent (performance guard)
- `OnEnd_NonToolSpan_DoesNotAddCausalAttributes` — spans with operation name `"chat"` or no operation name are not modified by this processor

The `IsAllDataRequested` flag is controlled by the `ActivityListener.Sample` delegate returning `ActivitySamplingResult.AllDataAndRecorded` vs `ActivitySamplingResult.PropagationData`. Create a second listener fixture for the "not all data requested" case.

### Trace Store Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/Traces/FileSystemExecutionTraceStoreTests.cs`

All tests use a temp directory (`Path.GetTempPath()` + a new Guid subdirectory) created in the constructor and deleted in `Dispose()`. The `FileSystemExecutionTraceStore` is constructed directly with a mock `ISecretRedactor` (passthrough by default) and a real `IOptions<MetaHarnessConfig>`.

Required test stubs:

```csharp
// Directory creation under correct subtree
StartRunAsync_WhenNoOptimizationId_CreatesRunDirectoryUnderExecutions()
StartRunAsync_WhenOptimizationIdProvided_CreatesRunDirectoryUnderOptimizations()

// Manifest written atomically with write_completed marker
StartRunAsync_WritesManifestJson_WithWriteCompletedTrue()
StartRunAsync_ManifestJson_ContainsCandidateId_WhenInScope()

// Turn artifacts
WriteTurnAsync_CreatesExpectedSubdirectoryWithAllArtifactFiles()

// JSONL trace appending
AppendTraceAsync_WritesValidJsonlLine_ToTracesFile()

// Concurrency — this is the critical correctness test
// Spawn 10 Tasks, each calling AppendTraceAsync 20 times on the same ITraceWriter.
// After all complete, read traces.jsonl and assert:
//   - Exactly 200 lines, each independently parseable as ExecutionTraceRecord JSON
//   - No partial/interleaved lines
//   - Sequence numbers are unique (not necessarily contiguous across tasks, but no duplicates)
AppendTraceAsync_ConcurrentWrites_DoNotCorruptJsonl()

// Redaction applied before writing
AppendTraceAsync_AppliesRedaction_WhenPayloadContainsSecret()

// Full payload splitting: if payload size > MaxFullPayloadKB, inline summary is truncated
// and payload_full_path points to a file in turns/{n}/tool_results/
AppendTraceAsync_IncludesPayloadFullPath_WhenPayloadExceedsInlineLimit()

// Atomic write: scores.json written via temp+rename; partial write never visible
WriteScoresAsync_WritesAtomically_ReadersNeverSeePartialJson()

// System prompt redaction
WriteTurnAsync_AppliesSecretRedactor_ToSystemPrompt()

// Path resolution
GetRunDirectoryAsync_ReturnsCorrectAbsolutePath()
```

---

## Implementation

### Deviations from Plan (Code Review Fixes Applied)

| Finding | Change from plan |
|---------|-----------------|
| H-1: Path traversal via `callId` | `SanitizeFileName` helper strips invalid chars; resolved path validated against `toolResultsDir` |
| H-2: Path traversal via `TaskId` | `TraceScope.ResolveDirectory` now validates `TaskId` rejects `..`, path separators, and `Path.GetInvalidFileNameChars()` |
| H-3: SemaphoreSlim never disposed | `ITraceWriter` now extends `IAsyncDisposable`; `FileSystemTraceWriter.DisposeAsync` disposes `_tracesLock` |
| H-4: Dead fields | `_agentName` and `_startedAt` removed from `FileSystemTraceWriter` |
| M-2+L-1: Duplicate helpers | `WriteAtomicAsync` and `JsonOptions` removed from nested class; uses outer class members |
| M-3: `CompleteAsync` JsonOptions | `JsonSerializer.Serialize(props, JsonOptions)` now passes options |
| M-5: Streaming not traced | `AppendFunctionResultTracesAsync` called at top of `GetStreamingResponseAsync` |
| M-6: Negative `turnNumber` | `ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turnNumber)` added |
| L-4: `IOptions` → `IOptionsMonitor` | Constructor takes `IOptionsMonitor<AppConfig>`; uses `CurrentValue` |
| N-1: Magic string `"__traceWriter"` | `ITraceWriter.AdditionalPropertiesKey` public const; used in factory |
| Q3-B: InputHash hashes wrong thing | Added `ToolCallArguments = "gen_ai.tool.call.arguments"` constant; processor hashes arguments not result |

### Actual File Paths Created/Modified

| File | Status |
|------|--------|
| `src/Content/Application/Application.AI.Common/Interfaces/Traces/ITraceWriter.cs` | Modified — added `IAsyncDisposable`, `AdditionalPropertiesKey` const |
| `src/Content/Application/Application.AI.Common/Interfaces/Traces/IExecutionTraceStore.cs` | Pre-existing |
| `src/Content/Infrastructure/Infrastructure.AI/Traces/FileSystemExecutionTraceStore.cs` | New |
| `src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanAttributionProcessor.cs` | Implemented (was stub) |
| `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs` | Modified |
| `src/Content/Application/Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs` | Modified |
| `src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs` | Modified — added `TraceScope?` property |
| `src/Content/Domain/Domain.AI/Skills/SkillAgentOptions.cs` | Modified — added `TraceScope?` property |
| `src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs` | Modified — added 6 constants |
| `src/Content/Domain/Domain.Common/MetaHarness/TraceScope.cs` | Modified — TaskId path validation |
| `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` | Modified — registered IExecutionTraceStore |
| `src/Content/Infrastructure/Infrastructure.Observability/Exporters/ObservabilityTelemetryConfigurator.cs` | Modified — added CausalSpanAttributionProcessor to pipeline |
| `src/Content/Tests/Infrastructure.AI.Tests/Traces/FileSystemExecutionTraceStoreTests.cs` | New — 12 tests |
| `src/Content/Tests/Infrastructure.Observability.Tests/Processors/CausalSpanAttributionProcessorTests.cs` | New — 8 tests |
| `src/Content/Tests/Application.AI.Common.Tests/Middleware/ToolDiagnosticsMiddlewareTests.cs` | New — 4 tests |
| `src/Content/Tests/Application.AI.Common.Tests/Factories/AgentExecutionContextFactoryTests.cs` | Modified — added 3 regression tests |

**Test count:** 27 new tests. All 895 tests pass.

### New Interfaces

#### `ITraceWriter`

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Traces/ITraceWriter.cs`

Extends `IAsyncDisposable`. Public const `AdditionalPropertiesKey = "__traceWriter"` for accessing the writer from `AgentExecutionContext.AdditionalProperties`.

```csharp
/// <summary>
/// Scoped writer for a single execution run. One instance per ExecutionRunId.
/// All methods are thread-safe. Sequence numbers are guaranteed monotonically
/// increasing across concurrent callers.
/// </summary>
public interface ITraceWriter : IAsyncDisposable
{
    /// <summary>The scope this writer was created for.</summary>
    TraceScope Scope { get; }

    /// <summary>Absolute path to the run directory on disk.</summary>
    string RunDirectory { get; }

    /// <summary>Writes all turn artifacts into turns/{turnNumber}/.</summary>
    Task WriteTurnAsync(int turnNumber, TurnArtifacts artifacts, CancellationToken ct = default);

    /// <summary>
    /// Appends one record to traces.jsonl. Thread-safe via internal SemaphoreSlim.
    /// Sequence number assigned via Interlocked.Increment on a per-writer counter.
    /// ISecretRedactor is applied to payload before writing.
    /// </summary>
    Task AppendTraceAsync(ExecutionTraceRecord record, CancellationToken ct = default);

    /// <summary>Atomically writes scores.json (temp + rename).</summary>
    Task WriteScoresAsync(HarnessScores scores, CancellationToken ct = default);

    /// <summary>Atomically writes summary.md.</summary>
    Task WriteSummaryAsync(string markdown, CancellationToken ct = default);

    /// <summary>
    /// Finalizes the run. Writes write_completed: true to manifest.json atomically.
    /// Must be called exactly once per writer instance.
    /// </summary>
    Task CompleteAsync(CancellationToken ct = default);
}
```

#### `IExecutionTraceStore`

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Traces/IExecutionTraceStore.cs`

```csharp
/// <summary>
/// Singleton store that creates per-run ITraceWriter instances.
/// The store itself holds no per-run state — all run state lives in the writer.
/// </summary>
public interface IExecutionTraceStore
{
    /// <summary>
    /// Creates the run directory, writes the initial manifest.json, and returns
    /// a scoped ITraceWriter. Callers must call CompleteAsync() when done.
    /// </summary>
    Task<ITraceWriter> StartRunAsync(TraceScope scope, RunMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Returns the absolute directory path for a given scope without creating it.
    /// Used by the proposer to locate trace directories for filesystem navigation.
    /// </summary>
    Task<string> GetRunDirectoryAsync(TraceScope scope, CancellationToken ct = default);
}
```

### Domain Types Referenced (from section-03)

These types must exist before implementing this section. They live in `Domain.Common/MetaHarness/`:

- `TraceScope` — `record` with `ExecutionRunId` (Guid), `OptimizationRunId` (Guid?), `CandidateId` (Guid?), `TaskId` (string?); factory `TraceScope.ForExecution(Guid)`
- `RunMetadata` — `record` with agent name, run description, timestamp, tags
- `TurnArtifacts` — `record` with `SystemPrompt` (string?), `ToolCallsJson` (string?), `ModelResponse` (string?), `StateSnapshotJson` (string?)
- `ExecutionTraceRecord` — `record` with all JSONL schema fields listed below
- `HarnessScores` — `record` with `PassRate` (double?), `TokenCost` (long?), `CustomScores` (`IReadOnlyDictionary<string, double>`)

### `ExecutionTraceRecord` JSONL Schema

Each line in `traces.jsonl` is a serialized `ExecutionTraceRecord`. JSON property names use `snake_case` (configure via `JsonSerializerOptions` with `JsonNamingPolicy.SnakeCaseLower`).

| C# Property | JSON Field | Type | Notes |
|---|---|---|---|
| `Seq` | `seq` | long | Monotonic per writer, set by writer not caller |
| `Timestamp` | `ts` | string | ISO 8601 |
| `Type` | `type` | string | `tool_call`, `tool_result`, `decision`, `observation` |
| `ExecutionRunId` | `execution_run_id` | string | Always set |
| `CandidateId` | `candidate_id` | string? | Optimization eval only |
| `Iteration` | `iteration` | int? | Optimization eval only |
| `TaskId` | `task_id` | string? | Eval task only |
| `TurnId` | `turn_id` | string | Required |
| `ToolName` | `tool_name` | string? | Tool events only |
| `ResultCategory` | `result_category` | string? | `success`, `partial`, `error`, `timeout`, `blocked` |
| `PayloadSummary` | `payload_summary` | string? | ≤500 chars, truncated |
| `PayloadFullPath` | `payload_full_path` | string? | Relative path to full artifact |
| `Redacted` | `redacted` | bool? | True if redaction was applied |

### `FileSystemExecutionTraceStore` Implementation

**File:** `src/Content/Infrastructure/Infrastructure.AI/Traces/FileSystemExecutionTraceStore.cs`

Constructor parameters: `IOptionsMonitor<AppConfig> appConfig`, `ISecretRedactor redactor`, `ILogger<FileSystemExecutionTraceStore> logger`

Key behaviors:

**Directory resolution** — `TraceScope → string path`:
- If `OptimizationRunId` is null: `{TraceDirectoryRoot}/executions/{ExecutionRunId}/`
- If `OptimizationRunId` is set and `CandidateId` is null: `{TraceDirectoryRoot}/optimizations/{OptimizationRunId}/executions/{ExecutionRunId}/`
- If both are set: `{TraceDirectoryRoot}/optimizations/{OptimizationRunId}/candidates/{CandidateId}/eval/{TaskId ?? "default"}/{ExecutionRunId}/`

**`StartRunAsync`**: resolve directory, `Directory.CreateDirectory`, write initial `manifest.json` atomically (with `"write_completed": false`), then construct and return a `FileSystemTraceWriter` instance.

**`GetRunDirectoryAsync`**: pure path resolution, no I/O.

**`FileSystemTraceWriter`** (private nested class or file-local sealed class in the same file):
- Constructor receives resolved `runDirectory`, `TraceScope`, `ISecretRedactor`, `IOptions<AppConfig>`
- `_sequenceCounter` — `long` field, incremented via `Interlocked.Increment`
- `_tracesLock` — `SemaphoreSlim(1, 1)` for serializing `traces.jsonl` appends
- `AppendTraceAsync`: acquire `_tracesLock`, assign sequence via `Interlocked.Increment(ref _sequenceCounter)`, apply redactor to `PayloadSummary`, write a single JSON line + newline to `traces.jsonl` using `File.AppendAllTextAsync` inside the semaphore
- `WriteTurnAsync`: no lock needed (each turn has its own subdirectory); apply redactor to `SystemPrompt`; write files; if tool result payload size > `MaxFullPayloadKB * 1024`, write full content to `turns/{n}/tool_results/{callId}.json` and set `PayloadFullPath`, truncate inline summary to 500 chars
- `WriteScoresAsync` / `WriteSummaryAsync`: write to temp file (`{path}.tmp`), then `File.Move(tmp, dest, overwrite: true)` (atomic on POSIX; best-effort on Windows)
- `CompleteAsync`: re-read `manifest.json`, set `write_completed: true`, atomic write

**Atomic write helper** (private static):
```csharp
private static async Task WriteAtomicAsync(string targetPath, string content)
{
    var tmp = targetPath + ".tmp";
    await File.WriteAllTextAsync(tmp, content);
    File.Move(tmp, targetPath, overwrite: true);
}
```

### ToolConventions Extension

**File:** `src/Content/Domain/Domain.AI/Telemetry/Conventions/ToolConventions.cs` (modify existing)

Add the following constants to the existing `ToolConventions` static class:

```csharp
// Causal attribution attributes (Meta-Harness)
/// <summary>SHA256 hex digest of serialized tool input. Only set when IsAllDataRequested.</summary>
public const string InputHash = "tool.input_hash";
/// <summary>Bucketed outcome category matching ExecutionTraceRecord.result_category.</summary>
public const string ResultCategory = "tool.result_category";
/// <summary>CandidateId from TraceScope when running inside an optimization eval.</summary>
public const string HarnessCandidateId = "gen_ai.harness.candidate_id";
/// <summary>Iteration number from TraceScope when running inside an optimization eval.</summary>
public const string HarnessIteration = "gen_ai.harness.iteration";
```

### `CausalSpanAttributionProcessor`

**File:** `src/Content/Infrastructure/Infrastructure.Observability/Processors/CausalSpanAttributionProcessor.cs`

Extends `BaseProcessor<Activity>`. Constructor: `ILogger<CausalSpanAttributionProcessor> logger`.

`OnEnd` logic:
1. Check `gen_ai.operation.name == "execute_tool"` — return early if not a tool span
2. Set `gen_ai.tool.name` from the existing `agent.tool.name` tag (bridge from project convention to OTel GenAI convention)
3. If `data.IsAllDataRequested`: compute SHA256 of the serialized input tag value (use `System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input))`), set `tool.input_hash` as lowercase hex
4. Read `result_category` from existing tag or derive from `data.Status`: `Error → "error"`, `Ok → "success"`, unset → `"unknown"`; set `tool.result_category`
5. Read `gen_ai.harness.candidate_id` from Activity baggage (not tags) — if present, set as tag; same for `gen_ai.harness.iteration`

Baggage is set by `AgentExecutionContextFactory` when building an eval context (see wiring section below).

### Wiring into `AgentExecutionContextFactory`

**File:** `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs` (modify)

Add constructor parameter: `IExecutionTraceStore traceStore`

On context creation:
1. If an optional `TraceScope` is passed in (for eval contexts), use it; otherwise call `TraceScope.ForExecution(Guid.NewGuid())`
2. Call `await traceStore.StartRunAsync(scope, metadata, ct)` → returns `ITraceWriter`
3. Store the writer on the context object (add `ITraceWriter TraceWriter { get; }` to `IAgentExecutionContext` if not already present)
4. If `scope.CandidateId` has a value, set Activity baggage: `Activity.Current?.SetBaggage(ToolConventions.HarnessCandidateId, scope.CandidateId.ToString())`; same for `HarnessIteration`

### Wiring into `ToolDiagnosticsMiddleware`

**File:** `src/Content/Application/Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs` (modify)

After each tool call completes, if `context.TraceWriter` is non-null:
1. Apply `ISecretRedactor` to the result string before including in the record
2. Build an `ExecutionTraceRecord` with `Type = "tool_result"`, `ToolName`, `ResultCategory` (derive from exception/success state), `PayloadSummary` (truncated to 500 chars)
3. Call `await context.TraceWriter.AppendTraceAsync(record, ct)`

The middleware should not throw if `AppendTraceAsync` fails — catch and log at Warning level to avoid breaking tool execution.

### DI Registration

**File:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` (modify)

```csharp
services.AddSingleton<IExecutionTraceStore, FileSystemExecutionTraceStore>();
```

**File:** `src/Content/Infrastructure/Infrastructure.Observability/DependencyInjection.cs` (modify)

Register `CausalSpanAttributionProcessor` and add it to the OTel tracer provider after the existing `ToolEffectivenessProcessor`.

### Directory Structure Reference

The filesystem layout that `FileSystemExecutionTraceStore` must produce:

```
{TraceDirectoryRoot}/
  executions/
    {executionRunId}/
      manifest.json          ← {executionRunId, agentName, startedAt, write_completed}
      traces.jsonl           ← one JSON object per line
      decisions.jsonl        ← written by section-06 (history store)
      scores.json            ← optional, written via WriteScoresAsync
      summary.md             ← optional, written via WriteSummaryAsync
      turns/
        {n}/
          system_prompt.md
          tool_calls.jsonl
          model_response.md
          state_snapshot.json
          tool_results/
            {callId}.json    ← only when payload > MaxFullPayloadKB
  optimizations/
    {optimizationRunId}/
      run_manifest.json      ← written by section-14 (outer loop handler)
      candidates/
        index.jsonl          ← written by section-10 (repository)
        {candidateId}/
          candidate.json     ← written by section-10
          snapshot/          ← written by section-14
          eval/
            {taskId}/
              {executionRunId}/
                manifest.json
                traces.jsonl
                ...
```

---

## Regression Tests

These existing behaviors must continue to work after modifications:

**`AgentExecutionContextFactory`** — existing non-optimization agent runs (no `TraceScope` passed in) must still work. The factory must create a `TraceScope.ForExecution(Guid.NewGuid())` when no scope is supplied. No existing call sites should break.

**`ToolDiagnosticsMiddleware`** — the new `AppendTraceAsync` call is additive. All existing tag-setting and metric-recording behavior must be preserved. The new call must not throw or affect existing behavior.

**`ToolEffectivenessProcessorTests`** — no changes to `ToolEffectivenessProcessor` itself; verify existing tests still pass after `ToolConventions` extension.

Add regression test stubs to the respective existing test files:

- `AgentExecutionContextFactoryTests` (wherever it lives): `CreateContext_WithoutTraceScope_CreatesForExecutionScope()`
- `ToolDiagnosticsMiddlewareTests` (wherever it lives): `InvokeNext_ToolCallCompletes_AppendsTraceRecord()`; `InvokeNext_AppendTraceThrows_DoesNotRethrow()`

---

## Checklist

1. Add `ITraceWriter` and `IExecutionTraceStore` interfaces in `Application.AI.Common/Interfaces/Traces/`
2. Add causal tag constants to `ToolConventions` in `Domain.AI`
3. Implement `FileSystemExecutionTraceStore` + `FileSystemTraceWriter` in `Infrastructure.AI/Traces/`
4. Implement `CausalSpanAttributionProcessor` in `Infrastructure.Observability/Processors/`
5. Modify `AgentExecutionContextFactory` to inject `IExecutionTraceStore` and start a run on context creation
6. Modify `ToolDiagnosticsMiddleware` to append trace records after tool calls
7. Register `IExecutionTraceStore` as singleton in `Infrastructure.AI/DependencyInjection.cs`
8. Register `CausalSpanAttributionProcessor` in `Infrastructure.Observability/DependencyInjection.cs`
9. Write `FileSystemExecutionTraceStoreTests` (all stubs above, concurrency test last)
10. Write `CausalSpanAttributionProcessorTests` (all stubs above)
11. Add regression test stubs to existing factory and middleware test files
12. `dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`
