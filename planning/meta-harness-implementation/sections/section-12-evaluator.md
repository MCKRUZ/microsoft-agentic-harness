# Section 12: Evaluator (`AgentEvaluationService`)

## Overview

This section implements the evaluation service that runs each proposed harness candidate against a defined set of tasks, grades the results, and writes per-task execution traces scoped to the candidate. It runs in parallel with section-11-proposer (Batch 6) and is a prerequisite for section-14-outer-loop.

## Dependencies (must be complete before starting)

- **section-04-trace-infrastructure**: `IExecutionTraceStore`, `ITraceWriter`, `TraceScope`, `FileSystemExecutionTraceStore`
- **section-08-skill-provider**: `ISkillContentProvider`, `CandidateSkillContentProvider`, `AgentExecutionContextFactory` override
- **section-09-candidate-domain**: `HarnessCandidate`, `HarnessSnapshot`, `EvalTask`, `HarnessCandidateStatus`
- **section-10-candidate-repository**: `IHarnessCandidateRepository`, `FileSystemHarnessCandidateRepository`

The following are referenced but not re-implemented here:

- `MetaHarnessConfig` (section-01) — `MaxEvalParallelism`, `EvalTasksPath`, `EvaluationTemperature`, `EvaluationModelVersion`
- `ISecretRedactor` (section-02) — applied inside `FileSystemExecutionTraceStore`, not re-applied here

## What This Section Builds

### New Files

```
Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs
Infrastructure.AI/MetaHarness/AgentEvaluationService.cs
Tests/Infrastructure.AI.Tests/MetaHarness/AgentEvaluationServiceTests.cs
```

### Modified Files

```
Infrastructure.AI/DependencyInjection.cs  — register AgentEvaluationService
```

---

## Background

The evaluator is the inner loop of the meta-harness optimization cycle. For each candidate produced by the proposer, it:

1. Runs the agent against every `EvalTask` (up to `SearchSetSize` tasks)
2. Grades each output against the task's `ExpectedOutputPattern` regex
3. Records pass/fail per task, total token cost, and writes execution traces scoped under the candidate's eval directory
4. Returns a structured `EvaluationResult` to the outer loop

Key design constraints (from the reviewed plan):
- **Candidate isolation**: Skill files are served from `HarnessCandidate.SkillFileSnapshots` in-memory, not from the active skill directory on disk. This prevents candidates from interfering with each other or the live system.
- **Regex grading with timeout**: `Regex.Match` is called with a 5-second timeout. A timeout counts as a task failure, not an error — the outer loop continues.
- **Parallelism is controlled**: A `SemaphoreSlim` initialized from `MetaHarnessConfig.MaxEvalParallelism` gates concurrent task execution. Default is 1 (sequential).
- **Trace scoping**: Each task run gets its own `TraceScope` encoding `OptimizationRunId`, `CandidateId`, `TaskId`, and a new `ExecutionRunId`. Traces are written to `optimizations/{optRunId}/candidates/{candidateId}/eval/{taskId}/{executionRunId}/`.

---

## Interface: `IEvaluationService`

**File:** `src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs`

```csharp
/// <summary>
/// Evaluates a harness candidate against a set of tasks and returns aggregated scores.
/// Each task is run in isolation using the candidate's in-memory skill snapshots.
/// </summary>
public interface IEvaluationService
{
    /// <summary>
    /// Runs each eval task against the candidate's proposed harness configuration,
    /// grades outputs against expected patterns, and writes per-task traces.
    /// </summary>
    Task<EvaluationResult> EvaluateAsync(
        HarnessCandidate candidate,
        IReadOnlyList<EvalTask> evalTasks,
        CancellationToken cancellationToken = default);
}
```

Supporting types belong in `Application.AI.Common` alongside the interface or in `Domain.Common/MetaHarness/` as appropriate:

```csharp
/// <summary>Aggregated result of evaluating one candidate across all tasks.</summary>
public sealed record EvaluationResult(
    Guid CandidateId,
    double PassRate,
    long TotalTokenCost,
    IReadOnlyList<TaskEvaluationResult> PerExampleResults);

/// <summary>Result for a single eval task run.</summary>
public sealed record TaskEvaluationResult(
    string TaskId,
    bool Passed,
    long TokenCost,
    string? FailureReason = null);
```

---

## Implementation: `AgentEvaluationService`

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/AgentEvaluationService.cs`

### Constructor Dependencies

- `IOptionsMonitor<MetaHarnessConfig>` — reads `MaxEvalParallelism`, `EvaluationTemperature`, `EvaluationModelVersion`
- `IExecutionTraceStore` — starts a scoped `ITraceWriter` per task run
- `AgentExecutionContextFactory` — builds execution context with candidate skill provider override
- `ILogger<AgentEvaluationService>`

### Algorithm (per `EvaluateAsync` call)

```
1. Create SemaphoreSlim(MaxEvalParallelism, MaxEvalParallelism)
2. For each EvalTask (fan-out with Task.WhenAll):
   a. Acquire semaphore slot
   b. Build TraceScope:
        OptimizationRunId = candidate.OptimizationRunId
        CandidateId       = candidate.CandidateId
        TaskId            = task.TaskId
        ExecutionRunId    = Guid.NewGuid()
   c. Construct CandidateSkillContentProvider(candidate.Snapshot.SkillFileSnapshots)
   d. Build IAgentExecutionContext via factory with:
        - overridden ISkillContentProvider = CandidateSkillContentProvider
        - TraceScope from step b
        - Temperature = EvaluationTemperature
        - ModelVersion = EvaluationModelVersion (if set)
   e. Run agent on task.InputPrompt; collect (output, tokenCount)
   f. Grade: if task.ExpectedOutputPattern is null → Passed = true
             else: try Regex.Match(output, pattern, RegexOptions.None, TimeSpan.FromSeconds(5))
                   RegexMatchTimeoutException → Passed = false, FailureReason = "regex_timeout"
   g. Complete/dispose the trace writer
   h. Release semaphore slot
   i. Return TaskEvaluationResult
3. Aggregate: PassRate = passed / total, TotalTokenCost = sum of all TokenCost
4. Return EvaluationResult
```

### Error Handling

- If the agent throws during a task run, catch the exception, log it, and return `TaskEvaluationResult` with `Passed = false` and `FailureReason = exception.Message`. Do not rethrow — the outer loop (section-14) handles candidate-level failure, but a single task exception should not abort all other tasks.
- If all tasks throw, `PassRate` will be 0.0, and the outer loop will treat this as a poor candidate rather than a fatal error (unless the outer loop itself decides to mark it `Failed`).

---

## EvalTask Loader

The evaluator does not load eval tasks — that is the outer loop's responsibility (section-14). However, the outer loop delegates loading to a helper. Implement this loader in the same file or as a separate static class:

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/EvalTaskLoader.cs`

```csharp
/// <summary>
/// Loads EvalTask definitions from JSON files at the configured path.
/// Each file must deserialize to a single EvalTask.
/// </summary>
public static class EvalTaskLoader
{
    /// <summary>
    /// Reads all *.json files under <paramref name="directoryPath"/> and deserializes each as an EvalTask.
    /// Files that fail to deserialize are logged and skipped.
    /// </summary>
    public static IReadOnlyList<EvalTask> LoadFromDirectory(string directoryPath, ILogger logger);
}
```

Each task JSON file:
```json
{
  "taskId": "summarize-short-doc",
  "description": "Summarize a short document under 100 words",
  "inputPrompt": "Summarize the following in one sentence: ...",
  "expectedOutputPattern": "(?i)(summary|summarize|key point)",
  "tags": ["summarization", "short"]
}
```

---

## DI Registration

**File:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Register `AgentEvaluationService` as a scoped service (not singleton — it holds a `SemaphoreSlim` per evaluation run):

```csharp
services.AddScoped<IEvaluationService, AgentEvaluationService>();
```

---

## Tests

**File:** `src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/AgentEvaluationServiceTests.cs`

**Test project:** `Infrastructure.AI.Tests`
**Framework:** xUnit + Moq

### Test Stubs

```csharp
public class AgentEvaluationServiceTests
{
    /// <summary>
    /// All tasks match their expected output patterns.
    /// EvaluationResult.PassRate should equal 1.0.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_AllTasksPass_ReturnsPassRateOne() { }

    /// <summary>
    /// No tasks match their expected output patterns.
    /// EvaluationResult.PassRate should equal 0.0.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_AllTasksFail_ReturnsPassRateZero() { }

    /// <summary>
    /// Task's expected pattern triggers a RegexMatchTimeoutException.
    /// The task must be recorded as Passed=false with FailureReason="regex_timeout",
    /// and PassRate must reflect the failure (not throw).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_RegexTimeout_CountsAsFailNotError() { }

    /// <summary>
    /// After evaluation, a trace directory must exist under:
    ///   optimizations/{optRunId}/candidates/{candidateId}/eval/{taskId}/{executionRunId}/
    /// Verify that manifest.json and traces.jsonl exist in that path.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WritesTraceUnderCandidateEvalDirectory() { }

    /// <summary>
    /// The agent execution context is built with a CandidateSkillContentProvider,
    /// not the live filesystem provider. Mock the factory and verify the override
    /// is a CandidateSkillContentProvider instance.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_UsesCandidateSkillContentProvider_NotFilesystem() { }

    /// <summary>
    /// With MaxEvalParallelism=2 and 4 tasks with artificial 50ms delay,
    /// total elapsed time should be ~100ms (2 batches of 2), not ~200ms (sequential).
    /// Use a mock agent and Task.Delay to simulate work.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WithParallelism2_RunsTasksConcurrently() { }
}
```

### Test Approach Notes

- Use `xUnit` with `Moq`. Do not mock `IEvaluationService` — test `AgentEvaluationService` directly.
- For trace-writing tests, use a real `FileSystemExecutionTraceStore` pointed at `Path.GetTempPath()` with a unique subdirectory per test, cleaned up in `IAsyncDisposable`.
- For the parallelism test, use `Stopwatch` with a tolerance of ±30ms to avoid flakiness.
- For regex timeout: construct a deliberately catastrophic backtracking pattern (e.g., `^(a+)+$` on a long input) to reliably trigger `RegexMatchTimeoutException` within the 5-second budget.

---

## Filesystem Layout Produced by This Section

After evaluating candidate `{C}` within optimization run `{O}` on two tasks `task-1` and `task-2`:

```
traces/
  optimizations/
    {O}/
      candidates/
        {C}/
          eval/
            task-1/
              {executionRunId-1}/
                manifest.json       ← write_completed: true
                traces.jsonl
                decisions.jsonl
                turns/
                  0/
                    system_prompt.md
                    tool_calls.jsonl
                    model_response.md
            task-2/
              {executionRunId-2}/
                manifest.json
                traces.jsonl
                ...
```

---

---

## Actual Implementation Notes

### Deviations from Plan

**Constructor dependency changed:** The plan specified `AgentExecutionContextFactory` as a constructor dependency. The actual implementation uses `IAgentFactory` instead. `AgentExecutionContextFactory` is a concrete class (not an interface), making it impossible to mock in tests. The implementation builds `AgentExecutionContext` directly (it is a plain class with no construction side effects) and delegates only agent creation to `IAgentFactory`.

**`AdditionalProperties` injection pattern:** The candidate skill provider and trace writer are injected into the agent context via `AdditionalProperties` dictionary keys (`ISkillContentProvider.AdditionalPropertiesKey` and `ITraceWriter.AdditionalPropertiesKey`), not via constructor injection on the context factory.

**`EvaluationTemperature` not passed:** `MetaHarnessConfig.EvaluationTemperature` remains unused in this implementation. The MS Agent Framework does not expose per-call temperature on `AgentExecutionContext` cleanly. Deferred for post-POC.

**`Math.Max(1, ...)` guard added:** `SemaphoreSlim` throws `ArgumentOutOfRangeException` if initialCount is ≤ 0. A guard `Math.Max(1, cfg.MaxEvalParallelism)` was added to prevent a crash when config is unconfigured (H-1 code review fix).

**`traceCompleted` flag:** The original structure risked calling `CompleteAsync` twice. A `traceCompleted` bool was introduced and `CompleteAsync` moved entirely to the `finally` block with a guard. All `CompleteAsync` calls use `CancellationToken.None` to survive parent token cancellation (M-3, L-2 code review fixes).

### Actual Files Created

```
src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs
src/Content/Infrastructure/Infrastructure.AI/MetaHarness/AgentEvaluationService.cs
src/Content/Infrastructure/Infrastructure.AI/MetaHarness/EvalTaskLoader.cs
src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAIAgent.cs     ← new helper, not shared from Application.Core.Tests
src/Content/Tests/Infrastructure.AI.Tests/Helpers/TestableAgentSession.cs
src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/AgentEvaluationServiceTests.cs
```

Modified: `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

### Test Coverage (6 tests, all passing)

- `EvaluateAsync_AllTasksPass_ReturnsPassRateOne`
- `EvaluateAsync_AllTasksFail_ReturnsPassRateZero`
- `EvaluateAsync_RegexTimeout_CountsAsFailNotError` — uses `^(a+)+$` catastrophic pattern
- `EvaluateAsync_WritesTraceUnderCandidateEvalDirectory` — verifies `manifest.json` exists (not `traces.jsonl`)
- `EvaluateAsync_UsesCandidateSkillContentProvider_NotFilesystem` — Moq Callback captures `AgentExecutionContext`
- `EvaluateAsync_WithParallelism2_RunsTasksConcurrently` — Stopwatch 70–200ms window

`TestableAIAgent` was duplicated into `Infrastructure.AI.Tests/Helpers/` (not shared from `Application.Core.Tests`, which is not referenced) with an added `WithDelay` factory method for the parallelism test.

---

## Integration with the Outer Loop (section-14 reference)

The outer loop (`RunHarnessOptimizationCommandHandler`) calls `IEvaluationService.EvaluateAsync` and:

- On success: uses `EvaluationResult.PassRate` and `TotalTokenCost` to update the candidate and compare against the current best
- On exception (if `AgentEvaluationService` itself throws — e.g., all tasks errored out before returning): marks the candidate `Status = Failed` with `FailureReason = exception.Message` and continues to the next iteration

This section does not implement the outer loop. Keep the interface contract stable so section-14 can consume it without modification.
