# Section 06: Agent History Store

## Overview

This section implements a queryable, append-only decision log that captures every meaningful agent decision event during execution. The history store writes `decisions.jsonl` into the trace run directory and exposes a `ReadHistoryTool` so the proposer agent can query prior execution history when generating improvements.

**Dependencies:**
- Section 04 (Trace Infrastructure) — `ITraceWriter`, `IExecutionTraceStore`, and the trace run directory structure must exist before this section.
- Section 11 (Proposer) consumes `ReadHistoryTool` and `IAgentHistoryStore` — do not proceed to section 11 until this section is complete.

**Parallelizable with:** section-05 (OTel spans), section-07 (skill extension), section-08 (skill provider).

---

## Files to Create

```
Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs
Infrastructure.AI/Memory/JsonlAgentHistoryStore.cs
Infrastructure.AI/Tools/ReadHistoryTool.cs
Tests/Infrastructure.AI.Tests/Memory/JsonlAgentHistoryStoreTests.cs
```

**Files to modify:**
```
Infrastructure.AI/DependencyInjection.cs   — register JsonlAgentHistoryStore and ReadHistoryTool
```

---

## Tests First

**Test project:** `Infrastructure.AI.Tests`
**File:** `src/Content/Tests/Infrastructure.AI.Tests/Memory/JsonlAgentHistoryStoreTests.cs`
**Naming convention:** `MethodName_Scenario_ExpectedResult`

Write all tests before implementing. Each test should be independently runnable. Use `xUnit` + `Moq`. Arrange-Act-Assert structure.

### `JsonlAgentHistoryStore` Tests

```csharp
// AppendAsync_WritesDecisionEventRecord_ToDecisionsJsonl
// Arrange: temp directory, real JsonlAgentHistoryStore instance, one AgentDecisionEvent
// Act: AppendAsync(event, ct)
// Assert: decisions.jsonl exists; its single line deserializes to the original event with matching fields

// AppendAsync_SequenceNumbers_AreMonotonicallyIncreasing
// Arrange: store, three events
// Act: AppendAsync three times sequentially
// Assert: sequence numbers are 1, 2, 3 (or 0,1,2 — must be strictly increasing)

// QueryAsync_NoFilters_ReturnsAllRecords
// Arrange: store with 5 appended events for the same ExecutionRunId
// Act: QueryAsync(new DecisionLogQuery { ExecutionRunId = id })
// Assert: returns all 5 events

// QueryAsync_FilterByEventType_ReturnsMatchingOnly
// Arrange: 3 events with EventType "tool_call", 2 with "decision"
// Act: QueryAsync with EventType = "tool_call"
// Assert: exactly 3 returned

// QueryAsync_FilterByToolName_ReturnsMatchingOnly
// Arrange: mix of events, 2 with ToolName "read_history"
// Act: QueryAsync with ToolName = "read_history"
// Assert: exactly 2 returned

// QueryAsync_WithSince_SkipsRecordsAtOrBeforeSequence
// Arrange: 5 events with sequences 1-5
// Act: QueryAsync with Since = 3
// Assert: returns only events with Sequence > 3 (sequences 4 and 5)

// QueryAsync_WithLimit_ReturnsBoundedResults
// Arrange: 10 appended events
// Act: QueryAsync with Limit = 3
// Assert: exactly 3 events returned

// AppendAsync_ConcurrentAppends_DoNotCorruptFile
// Arrange: store, 10 concurrent tasks each appending 5 events (50 total)
// Act: Task.WhenAll(tasks)
// Assert: decisions.jsonl has exactly 50 valid, parseable lines; no interleaving corruption
```

### `ReadHistoryTool` Tests

Write in the same test file or a sibling `ReadHistoryToolTests.cs`:

```csharp
// Execute_WithValidRunId_ReturnsSerializedEvents
// Arrange: store with 3 events, ReadHistoryTool wrapping it
// Act: Execute({ execution_run_id: id })
// Assert: result JSON parses to array of 3 elements

// Execute_WithSinceParameter_OnlyReturnsNewerEvents
// Arrange: store with 5 events (sequences 1-5)
// Act: Execute({ execution_run_id: id, since: 3 })
// Assert: result contains 2 elements (sequences 4 and 5)

// Execute_ExceedsLimit_TruncatesToLimit
// Arrange: store with 20 events
// Act: Execute({ execution_run_id: id, limit: 5 })
// Assert: result contains exactly 5 elements

// Execute_InvalidRunId_ReturnsEmptyArray
// Arrange: store with no events for the queried run ID
// Act: Execute({ execution_run_id: "nonexistent-id" })
// Assert: result is "[]" or an empty JSON array (no exception thrown)
```

---

## Domain Records

### `AgentDecisionEvent`

**File:** `Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs` (or a co-located `AgentDecisionEvent.cs`)

Immutable record capturing one agent decision. All properties are init-only.

| Property | Type | Notes |
|---|---|---|
| `Sequence` | `long` | Monotonically increasing per store instance |
| `Timestamp` | `DateTimeOffset` | UTC |
| `EventType` | `string` | `"tool_call"`, `"tool_result"`, `"decision"`, `"observation"` |
| `ExecutionRunId` | `string` | Correlation to the trace run |
| `TurnId` | `string` | Which conversation turn |
| `ToolName` | `string?` | Populated for tool events |
| `ResultCategory` | `string?` | `"success"`, `"partial"`, `"error"`, `"timeout"`, `"blocked"` |
| `Payload` | `JsonElement?` | Optional structured payload |

### `DecisionLogQuery`

Record with: `ExecutionRunId` (required string), optional `TurnId` (string?), optional `EventType` (string?), optional `ToolName` (string?), `Since` (long, default 0), `Limit` (int, default 100).

---

## Interface

### `IAgentHistoryStore`

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs`

```csharp
/// <summary>
/// Append-only, queryable log of agent decision events for a single execution run.
/// Written to decisions.jsonl in the trace run directory.
/// </summary>
public interface IAgentHistoryStore
{
    /// <summary>Appends a decision event. Thread-safe.</summary>
    Task AppendAsync(AgentDecisionEvent evt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams matching events. Filters applied in order: ExecutionRunId,
    /// EventType, ToolName, Since (sequence checkpoint). Bounded by Limit.
    /// </summary>
    IAsyncEnumerable<AgentDecisionEvent> QueryAsync(
        DecisionLogQuery query,
        CancellationToken cancellationToken = default);
}
```

---

## Implementation

### `JsonlAgentHistoryStore`

**File:** `src/Content/Infrastructure/Infrastructure.AI/Memory/JsonlAgentHistoryStore.cs`

Key implementation details:

- Constructor takes `ITraceWriter` (to resolve the run directory path and share the per-directory lock). The `ITraceWriter` was already designed to own a `SemaphoreSlim` for thread-safe JSONL writes in the same directory — share that lock for `decisions.jsonl` writes.
- `AppendAsync`: serialize the `AgentDecisionEvent` to a JSON line, acquire the semaphore, write the line + newline, release. Assign `Sequence` via `Interlocked.Increment` on a private `long _sequence = 0` field before acquiring the lock (the sequence is assigned before the lock, but the write ordering is serialized by the semaphore).
- `QueryAsync`: open `decisions.jsonl` with `FileShare.ReadWrite`, stream line-by-line, deserialize each line, apply predicates (ExecutionRunId match, EventType match if set, ToolName match if set, Sequence > Since), yield until Limit reached.
- Use `System.Text.Json` for all serialization. Property names in JSONL should use camelCase (`snake_case` is also acceptable — pick one and be consistent with `ExecutionTraceRecord` from section 04).
- If `decisions.jsonl` does not exist when `QueryAsync` is called, return an empty async enumerable (do not throw).

Stub definition:

```csharp
/// <summary>
/// Writes AgentDecisionEvent records to decisions.jsonl in the trace run directory.
/// Shares the ITraceWriter's directory-level semaphore for corruption-safe concurrent appends.
/// </summary>
public sealed class JsonlAgentHistoryStore : IAgentHistoryStore
{
    private long _sequence;
    private readonly ITraceWriter _traceWriter;

    public JsonlAgentHistoryStore(ITraceWriter traceWriter) { ... }

    public Task AppendAsync(AgentDecisionEvent evt, CancellationToken cancellationToken = default) { ... }

    public IAsyncEnumerable<AgentDecisionEvent> QueryAsync(
        DecisionLogQuery query,
        CancellationToken cancellationToken = default) { ... }
}
```

### `ReadHistoryTool`

**File:** `src/Content/Infrastructure/Infrastructure.AI/Tools/ReadHistoryTool.cs`

Key implementation details:

- Keyed registration key: `"read_history"`
- Tool schema (JSON input parameters):
  - `execution_run_id` (string, required) — which run to query
  - `event_type` (string, optional) — filter by event type
  - `tool_name` (string, optional) — filter by tool name
  - `since` (int, optional, default 0) — sequence checkpoint
  - `limit` (int, optional, default 100) — max results
- On execution: build a `DecisionLogQuery` from the parameters, call `IAgentHistoryStore.QueryAsync`, collect to list, serialize to JSON array string. Return the JSON string as the tool result.
- If `execution_run_id` is missing or empty: return `"[]"` (do not throw — the proposer agent shouldn't crash on a bad call).
- If `execution_run_id` has no matching records: return `"[]"`.

Stub definition:

```csharp
/// <summary>
/// Tool keyed "read_history". Queries the agent decision log for a specific execution run.
/// Returns a JSON array of AgentDecisionEvent records. Safe to call with unknown run IDs.
/// </summary>
public sealed class ReadHistoryTool : IAgentTool  // use the existing IAgentTool interface
{
    public string Name => "read_history";

    private readonly IAgentHistoryStore _historyStore;

    public ReadHistoryTool(IAgentHistoryStore historyStore) { ... }

    public Task<string> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken = default) { ... }
}
```

Note: check `Application.AI.Common/Interfaces/` for the correct `IAgentTool` or equivalent tool interface already in use in the codebase. Use that interface, not a new one.

---

## Dependency Injection

**File:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Add to the existing `AddInfrastructureAIDependencies()` extension method:

```csharp
// IAgentHistoryStore is scoped — one instance per ITraceWriter (per execution run)
services.AddScoped<IAgentHistoryStore, JsonlAgentHistoryStore>();

// ReadHistoryTool registered as keyed singleton
services.AddKeyedSingleton<IAgentTool, ReadHistoryTool>("read_history");
```

Note: `IAgentHistoryStore` should be `Scoped` if the `ITraceWriter` is scoped per execution. Verify that scoping aligns with how `AgentExecutionContextFactory` (section 04) creates and stores the `ITraceWriter`.

---

## Interaction with Trace Infrastructure (Section 04)

`JsonlAgentHistoryStore` writes to the same directory as `FileSystemExecutionTraceStore`. To avoid JSONL corruption when both `traces.jsonl` and `decisions.jsonl` are being written concurrently from separate components:

- `ITraceWriter` should expose either:
  - A `SemaphoreSlim GetFileLock(string fileName)` method that returns a named per-file lock, or
  - A public `SemaphoreSlim DirectoryLock` property that both `AppendTraceAsync` and `AppendAsync` share
- Choose the named-per-file approach if section 04 didn't already expose a lock — it keeps contention lower since `traces.jsonl` and `decisions.jsonl` don't need to block each other.

If `ITraceWriter` from section 04 already has a `SemaphoreSlim` for its own writes, add a `GetFileLockAsync(string relativeFileName)` method (or equivalent) to `ITraceWriter` rather than adding a new lock in `JsonlAgentHistoryStore`. Minimize new abstractions.

---

## Verification

After implementation, run:

```
dotnet build src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~JsonlAgentHistoryStore|FullyQualifiedName~ReadHistoryTool"
```

All 12 tests listed above must pass. Concurrent append test (`AppendAsync_ConcurrentAppends_DoNotCorruptFile`) is the most critical — it catches missing lock sharing between components.

---

## Implementation Notes (Actual vs. Planned)

**Interface location:** `Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs` — `AgentDecisionEvent`, `DecisionLogQuery`, and `IAgentHistoryStore` all in one file.

**Tool interface:** `ReadHistoryTool` implements `ITool` (not `IAgentTool` as spec noted — `ITool` is the correct interface in this codebase). Operations: `["query"]`.

**Lock strategy:** `JsonlAgentHistoryStore` uses its own private `SemaphoreSlim` (separate from `ITraceWriter`'s internal lock). No lock sharing needed — `decisions.jsonl` and `traces.jsonl` don't block each other.

**DI registration:** `IAgentHistoryStore` is NOT registered as scoped in DI — `ITraceWriter` is not in the container. Instead, a factory delegate `Func<ITraceWriter, IAgentHistoryStore>` is registered as singleton. `AgentExecutionContextFactory` (section 14) uses the factory to create paired instances.

**`IDisposable`:** Added to `JsonlAgentHistoryStore` — disposes the `SemaphoreSlim`. Section-14 is responsible for calling `Dispose()` alongside `ITraceWriter.DisposeAsync()`.

**Parameter safety:** `ReadHistoryTool` uses `TryParse`-based helpers instead of `Convert.ToInt*` for LLM-provided `since`/`limit` parameters — safe for malformed input.

**Tests:** 17 total (planned 12 + 5 added):
- `QueryAsync_FilterByTurnId_ReturnsMatchingOnly` — TurnId filter (M-4 from code review)
- `QueryAsync_WithCorruptedLine_SkipsCorruptedAndReturnsValid` — defensive JSON parse (M-5)
- `Execute_MissingRunId_ReturnsEmptyArray` — missing execution_run_id
- `Execute_UnsupportedOperation_ReturnsFail` — bad operation name
- `Execute_WithSinceParameter_OnlyReturnsNewerEvents` — since filter on tool layer
