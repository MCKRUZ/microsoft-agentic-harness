# Section 03: Trace Domain Value Objects

## Overview

This section implements the pure domain value objects that form the identity and data model for execution trace persistence. There is no I/O in this section — all types are plain C# records with path-resolution logic and no external dependencies. These types are consumed by section 04 (trace infrastructure) and all subsequent sections that deal with trace data.

**Depends on:** section-01-config (`MetaHarnessConfig` must exist for path resolution context)

**Blocks:** section-04-trace-infrastructure

---

## Files Created

All files placed under the `Domain.Common` project as planned:

```
src/Content/Domain/Domain.Common/MetaHarness/TraceScope.cs
src/Content/Domain/Domain.Common/MetaHarness/RunMetadata.cs
src/Content/Domain/Domain.Common/MetaHarness/TurnArtifacts.cs
src/Content/Domain/Domain.Common/MetaHarness/ExecutionTraceRecord.cs
src/Content/Domain/Domain.Common/MetaHarness/HarnessScores.cs
```

**Test file** (Domain.Common.Tests existed):

```
src/Content/Tests/Domain.Common.Tests/MetaHarness/TraceScopeTests.cs
```

## Deviations from Plan

**Guard clauses added to TraceScope (not in original spec):** Code review identified that silent invalid combinations (`CandidateId` without `OptimizationRunId`, `TaskId` without `CandidateId`, `Guid.Empty` `ExecutionRunId`) would silently produce wrong trace paths, causing orphaned trace data. Guards were added to `ForExecution` and `ResolveDirectory`. See `section-03-interview.md`.

## Final Test Count

9 tests in `TraceScopeTests` (6 planned + 3 added for guard clause coverage). All pass.

---

## Tests First

**Test project:** `Domain.Common.Tests` (or `Application.AI.Common.Tests`)

All tests are unit tests with no mocking required — these are pure value objects.

```csharp
// TraceScopeTests.cs
public class TraceScopeTests
{
    // ForExecution factory creates a scope where OptimizationRunId and CandidateId are null,
    // TaskId is null, and ExecutionRunId matches the supplied Guid.
    [Fact]
    public void ForExecution_CreatesScope_WithNullOptimizationAndCandidateIds() { }

    // A scope with only ExecutionRunId should resolve to:
    // "{traceRoot}/executions/{executionRunId}"
    [Fact]
    public void ResolveDirectory_WithExecutionOnlyScope_ResolvesUnderExecutions() { }

    // A scope with OptimizationRunId + CandidateId + ExecutionRunId + TaskId should resolve to:
    // "{traceRoot}/optimizations/{optRunId}/candidates/{candidateId}/eval/{taskId}/{executionRunId}"
    [Fact]
    public void ResolveDirectory_WithAllIds_ResolvesToCorrectDirectoryPath() { }

    // A scope with OptimizationRunId but no CandidateId (run-level scope) should resolve to:
    // "{traceRoot}/optimizations/{optRunId}"
    [Fact]
    public void ResolveDirectory_WithOptimizationOnlyScope_ResolvesUnderOptimizations() { }

    // A scope with OptimizationRunId + CandidateId but no TaskId should resolve to:
    // "{traceRoot}/optimizations/{optRunId}/candidates/{candidateId}"
    [Fact]
    public void ResolveDirectory_WithOptimizationAndCandidateButNoTask_ResolvesCorrectly() { }

    // Verify TraceScope is an immutable record — with expressions produce new instances
    [Fact]
    public void TraceScope_WithExpression_DoesNotMutateOriginal() { }
}
```

---

## Implementation Details

### `TraceScope`

**File:** `Domain.Common/MetaHarness/TraceScope.cs`

Immutable record that encodes the three-tier identity model. All filesystem path resolution for trace output is computed from a `TraceScope` instance. No I/O — path resolution is a pure string operation.

The three identity tiers are:
- `OptimizationRunId` (`Guid?`) — the outer loop run; null for non-optimization agent runs
- `CandidateId` (`Guid?`) — one proposed harness configuration; null for non-optimization runs and run-level paths
- `ExecutionRunId` (`Guid`) — always required; identifies a single agent execution
- `TaskId` (`string?`) — identifies which eval task this execution belongs to; null for non-eval runs

**Static factory:** `TraceScope.ForExecution(Guid executionRunId)` — creates a standalone scope with all optional fields null. This is the factory used for normal (non-optimization) agent runs.

**Path resolution method:** `ResolveDirectory(string traceRoot)` — returns an absolute path string. Resolution rules:

| Scope fields set | Resolved path |
|---|---|
| `ExecutionRunId` only | `{traceRoot}/executions/{executionRunId}` |
| `OptimizationRunId` only | `{traceRoot}/optimizations/{optRunId}` |
| `OptimizationRunId` + `CandidateId` | `{traceRoot}/optimizations/{optRunId}/candidates/{candidateId}` |
| `OptimizationRunId` + `CandidateId` + `TaskId` + `ExecutionRunId` | `{traceRoot}/optimizations/{optRunId}/candidates/{candidateId}/eval/{taskId}/{executionRunId}` |

Use `Path.Combine` for all path construction. All Guid fields should be formatted lowercase without braces (`"N"` format or `.ToString("D").ToLowerInvariant()`). Consistent casing is critical since these paths are referenced by the proposer via filesystem tools.

Stub signature:

```csharp
/// <summary>
/// Encodes the three-tier identity (OptimizationRun → Candidate → Execution) and
/// resolves filesystem paths for trace output. Immutable record; no I/O.
/// </summary>
public sealed record TraceScope
{
    public Guid ExecutionRunId { get; init; }
    public Guid? OptimizationRunId { get; init; }
    public Guid? CandidateId { get; init; }
    public string? TaskId { get; init; }

    /// <summary>Creates a standalone execution scope (non-optimization agent run).</summary>
    public static TraceScope ForExecution(Guid executionRunId) => ...;

    /// <summary>
    /// Returns the absolute directory path for this scope under <paramref name="traceRoot"/>.
    /// Pure string operation — no I/O.
    /// </summary>
    public string ResolveDirectory(string traceRoot) => ...;
}
```

---

### `RunMetadata`

**File:** `Domain.Common/MetaHarness/RunMetadata.cs`

Immutable record carrying metadata written to `manifest.json` when a run is started. Fields:

| Property | Type | Description |
|---|---|---|
| `StartedAt` | `DateTimeOffset` | When the run started |
| `AgentName` | `string` | Name of the agent being traced |
| `TaskDescription` | `string?` | Optional human-readable description of the task |
| `CandidateId` | `Guid?` | Set for optimization eval runs |
| `OptimizationRunId` | `Guid?` | Set for optimization eval runs |
| `Iteration` | `int?` | Set for optimization eval runs |
| `TaskId` | `string?` | Set for eval task runs |

This is a plain data record. No behavior beyond what `record` provides.

---

### `TurnArtifacts`

**File:** `Domain.Common/MetaHarness/TurnArtifacts.cs`

Immutable record representing everything written to a `turns/{n}/` subdirectory. Fields:

| Property | Type | Description |
|---|---|---|
| `TurnNumber` | `int` | 1-based turn index |
| `SystemPrompt` | `string?` | Contents of `system_prompt.md` |
| `ToolCallsJsonl` | `string?` | Raw JSONL string written to `tool_calls.jsonl` |
| `ModelResponse` | `string?` | Contents of `model_response.md` |
| `StateSnapshot` | `string?` | JSON string written to `state_snapshot.json` |
| `ToolResults` | `IReadOnlyDictionary<string, string>` | Map of `callId → serialized result` written to `tool_results/{callId}.json` |

`ToolResults` defaults to an empty dictionary. All properties are nullable — a turn artifact may contain only a subset of files.

---

### `ExecutionTraceRecord`

**File:** `Domain.Common/MetaHarness/ExecutionTraceRecord.cs`

Immutable record representing one JSONL line in `traces.jsonl`. Fields match the schema defined in section 04. Use `JsonPropertyName` attributes to ensure snake_case serialization.

| Property | Type | JSON field |
|---|---|---|
| `Seq` | `long` | `seq` |
| `Ts` | `DateTimeOffset` | `ts` |
| `Type` | `string` | `type` |
| `ExecutionRunId` | `string` | `execution_run_id` |
| `CandidateId` | `string?` | `candidate_id` |
| `Iteration` | `int?` | `iteration` |
| `TaskId` | `string?` | `task_id` |
| `TurnId` | `string` | `turn_id` |
| `ToolName` | `string?` | `tool_name` |
| `ResultCategory` | `string?` | `result_category` |
| `PayloadSummary` | `string?` | `payload_summary` |
| `PayloadFullPath` | `string?` | `payload_full_path` |
| `Redacted` | `bool?` | `redacted` |

Valid `Type` values: `"tool_call"`, `"tool_result"`, `"decision"`, `"observation"`.

Valid `ResultCategory` values: `"success"`, `"partial"`, `"error"`, `"timeout"`, `"blocked"`.

Define these as `public static class` constants in the same file or as adjacent static classes `TraceRecordTypes` and `TraceResultCategories` — do not use magic strings at call sites.

---

### `HarnessScores`

**File:** `Domain.Common/MetaHarness/HarnessScores.cs`

Immutable record representing the contents of `scores.json`. Fields:

| Property | Type | Description |
|---|---|---|
| `PassRate` | `double` | 0.0–1.0 |
| `TotalTokenCost` | `long` | Cumulative tokens for this run |
| `PerExampleResults` | `IReadOnlyList<ExampleResult>` | Per-task pass/fail breakdown |
| `ScoredAt` | `DateTimeOffset` | When scoring was completed |

`ExampleResult` is a nested immutable record (or can be a separate file):

| Property | Type |
|---|---|
| `TaskId` | `string` |
| `Passed` | `bool` |
| `TokenCost` | `long` |

---

## Design Notes

**Why pure domain?** These types have no I/O dependencies and can be unit-tested without any infrastructure setup. Path resolution logic in `TraceScope.ResolveDirectory` is the most critical piece — errors here cascade to every downstream file write.

**Guid formatting:** Use lowercase hyphenated format (`.ToString("D").ToLowerInvariant()` or just the default `ToString()` which is already lowercase-hyphenated on .NET). Do not use `"B"` or `"P"` formats. Consistent formatting is required because the proposer agent navigates these paths using filesystem tools and string comparison.

**Immutability:** All records must use `init`-only properties. `IReadOnlyList<T>` and `IReadOnlyDictionary<K,V>` for collection surfaces. No setters, no mutable fields.

**Serialization:** `ExecutionTraceRecord` is the only type that needs `[JsonPropertyName]` attributes since it is written directly to JSONL. Other types are serialized by `System.Text.Json` with default settings (PascalCase property names map to PascalCase JSON) — consistent with the rest of the project.

**No validation here:** Input validation (e.g., ensuring `ExecutionRunId` is not empty) belongs in FluentValidation on the command layer, not in domain records. Domain records are trusted internal objects.
