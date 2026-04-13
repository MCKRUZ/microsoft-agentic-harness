# Section 14: Outer Loop — `RunHarnessOptimizationCommand`

## Overview

This section implements the outer optimization loop: the MediatR command, its FluentValidation validator, and the command handler that orchestrates the full iteration cycle. The handler ties together all prior sections — it calls the proposer, runs evaluation, scores candidates, manages run state, and writes the final `_proposed/` output.

This section depends on all of sections 9–13 being complete:
- `HarnessCandidate`, `HarnessCandidateStatus`, `EvalTask` (section-09-candidate-domain)
- `IHarnessCandidateRepository` / `FileSystemHarnessCandidateRepository` (section-10-candidate-repository)
- `IHarnessProposer` / `OrchestratedHarnessProposer`, `HarnessProposalParsingException` (section-11-proposer)
- `IEvaluationService` / `AgentEvaluationService` (section-12-evaluator)
- `ISnapshotBuilder` / `ActiveConfigSnapshotBuilder` (section-09-candidate-domain)
- `RestrictedSearchTool`, `TraceResourceProvider` (section-13-tools)
- `MetaHarnessConfig` (section-01-config), `ISecretRedactor` (section-02-secret-redaction)

Section-15-console-ui depends on this section.

---

## Files Created

```
src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs
src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandValidator.cs
src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs
src/Content/Tests/Application.Core.Tests/CQRS/MetaHarness/RunHarnessOptimizationCommandHandlerTests.cs
```

## Deviations from Plan

1. **`IterationCount` uses executed counter, not planned count** — Added `executedIterations` counter incremented at the top of each loop iteration (including failures). Replaces `Math.Max(0, maxIterations - startIteration + 1)` which was unreliable on cancellation.
2. **`SafeResolvePath` helper added** — `WriteSnapshotFiles` and `WriteProposedSnapshot` now validate all skill file paths stay within the snapshot directory via `Path.GetFullPath` + `StartsWith`. Defends against LLM-generated path traversal in proposals.
3. **`LoadOrCreateRunManifest` made async** — Switched to `File.ReadAllTextAsync`. Post-review fix.
4. **Bare catches narrowed** — `LoadEvalTasksAsync` and `LoadOrCreateRunManifest` catch only `JsonException or IOException`. Non-permission/OOM errors now propagate.
5. **`EnforceRetentionPolicy` adds `Guid.TryParse` filter** — Only GUID-named directories are candidates for deletion.
6. **`SeedCandidateId` throws when not found** — Explicit resume intent should not silently fall back to a new seed.
7. **`RunManifest.write_completed` renamed to `writeCompleted`** — Consistent camelCase JSON property naming.
8. **`ProposedChangesPath` returns empty string when no best candidate** — No longer returns path to a non-existent directory.
9. **Handler is 530+ lines** — Exceeds the 400-line convention. Not refactored into helper classes; this is a POC and extracting 3 helper classes for a single consumer would be premature abstraction.

## Final Test Count: 13 tests, all passing

---

## Tests First

**Test project:** `Tests/Application.Core.Tests/CQRS/MetaHarness/RunHarnessOptimizationCommandHandlerTests.cs`

All dependencies are mocked via Moq. The handler receives `IHarnessProposer`, `IEvaluationService`, `IHarnessCandidateRepository`, `ISnapshotBuilder`, `IOptionsMonitor<MetaHarnessConfig>`, `ILogger<RunHarnessOptimizationCommandHandler>`, and a filesystem abstraction for `run_manifest.json` / `_proposed/` I/O.

### Test stubs

```csharp
/// <summary>
/// Unit tests for RunHarnessOptimizationCommandHandler.
/// All external collaborators are mocked. Filesystem I/O for manifests and
/// _proposed/ output uses a temp directory created per test.
/// </summary>
public class RunHarnessOptimizationCommandHandlerTests : IDisposable
{
    // Arrange helpers: BuildHandler(), BuildCommand(), MockProposer(), MockEvaluator()
    // Shared temp dir per test via Path.GetTempFileName() → delete+mkdir pattern

    [Fact]
    public async Task Handle_ExecutesMaxIterations_WhenAllSucceed() { }

    [Fact]
    public async Task Handle_ProposerParsingFailure_MarksFailedAndContinues() { }

    [Fact]
    public async Task Handle_EvaluationException_MarksFailedAndContinues() { }

    [Fact]
    public async Task Handle_FailuresCountAsIterations_NotSkipped() { }

    [Fact]
    public async Task Handle_ScoreBelowThreshold_DoesNotUpdateBest() { }

    [Fact]
    public async Task Handle_TieOnPassRate_PicksLowerTokenCostCandidate() { }

    [Fact]
    public async Task Handle_TieOnBoth_PicksEarlierIterationCandidate() { }

    [Fact]
    public async Task Handle_ResumesFromManifest_SkipsAlreadyCompletedIterations() { }

    [Fact]
    public async Task Handle_WritesRunManifestAfterEachIteration() { }

    [Fact]
    public async Task Handle_WritesProposedChangesToOutputDir_AtEnd() { }

    [Fact]
    public async Task Handle_CancellationRequested_StopsCleanlyBetweenIterations() { }

    [Fact]
    public async Task Handle_RetentionPolicy_DeletesOldestRunsWhenExceedsMaxRunsToKeep() { }

    [Fact]
    public async Task Handle_NoEvalTasks_ReturnsValidationFailure() { }

    public void Dispose() { /* delete temp dir */ }
}
```

All tests follow Arrange-Act-Assert. Mock the proposer to return a scripted `HarnessProposal` per iteration; mock the evaluator to return a scripted `EvaluationResult`. Use a real temp directory for manifest/output assertions.

---

## Implementation

### `RunHarnessOptimizationCommand`

**File:** `src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs`

MediatR `IRequest<OptimizationResult>` record.

| Property | Type | Notes |
|---|---|---|
| `OptimizationRunId` | `Guid` | Identifies this outer loop run; must not be empty |
| `SeedCandidateId` | `Guid?` | Optional — resume from a prior candidate's snapshot |
| `MaxIterations` | `int?` | If set, overrides `MetaHarnessConfig.MaxIterations` |

`OptimizationResult` is a return value record defined in the same file or a sibling:

| Property | Type |
|---|---|
| `OptimizationRunId` | `Guid` |
| `BestCandidateId` | `Guid?` |
| `BestScore` | `double` |
| `IterationCount` | `int` |
| `ProposedChangesPath` | `string` |

### `RunHarnessOptimizationCommandValidator`

**File:** `src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandValidator.cs`

FluentValidation `AbstractValidator<RunHarnessOptimizationCommand>`.

Rules:
- `OptimizationRunId` must not be `Guid.Empty`
- `MaxIterations` (when provided) must be `> 0`

### `RunHarnessOptimizationCommandHandler`

**File:** `src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs`

MediatR `IRequestHandler<RunHarnessOptimizationCommand, OptimizationResult>`.

Constructor dependencies:
- `IHarnessProposer _proposer`
- `IEvaluationService _evaluationService`
- `IHarnessCandidateRepository _candidateRepository`
- `ISnapshotBuilder _snapshotBuilder`
- `IOptionsMonitor<MetaHarnessConfig> _config`
- `ILogger<RunHarnessOptimizationCommandHandler> _logger`

#### Handler pseudocode

```
SETUP:
  config = _config.CurrentValue
  maxIterations = command.MaxIterations ?? config.MaxIterations
  runDir = Path.Combine(config.TraceDirectoryRoot, "optimizations", command.OptimizationRunId.ToString())
  Directory.CreateDirectory(runDir)
  evalTasks = LoadEvalTasksAsync(config.EvalTasksPath)
  if evalTasks is empty → return ValidationFailure result
  
  runManifest = LoadOrCreateRunManifest(runDir)   // reads run_manifest.json if present
  startIteration = runManifest.LastCompletedIteration + 1
  
  EnforceRetentionPolicy(config.MaxRunsToKeep, config.TraceDirectoryRoot)
  
  seedCandidate = await ResolveSeedCandidateAsync(command, config)
  currentCandidate = seedCandidate

LOOP for i in startIteration..maxIterations (inclusive):
  ct.ThrowIfCancellationRequested()

  // Step 1: Propose
  HarnessProposal proposal;
  try:
    proposerContext = new HarnessProposerContext(currentCandidate, runDir, priorCandidateIds, i)
    proposal = await _proposer.ProposeAsync(proposerContext, ct)
  catch HarnessProposalParsingException ex:
    failedCandidate = CreateFailedCandidate(currentCandidate, i, ex.Message, ex.RawOutput)
    await _candidateRepository.SaveAsync(failedCandidate, ct)
    _logger.LogWarning("Iteration {i}: proposer parsing failure — {msg}", i, ex.Message)
    UpdateRunManifest(runDir, i, currentBestCandidateId)
    continue

  // Step 2: Create candidate from proposal
  snapshot = BuildSnapshotFromProposal(currentCandidate.Snapshot, proposal)
  candidate = new HarnessCandidate { ..., Status = Proposed, Iteration = i, ... }
  await _candidateRepository.SaveAsync(candidate, ct)
  WriteSnapshotFiles(runDir, candidate)

  // Step 3: Evaluate
  EvaluationResult evalResult;
  try:
    evalResult = await _evaluationService.EvaluateAsync(candidate, evalTasks, ct)
  catch Exception ex:
    failed = candidate with { Status = Failed, FailureReason = ex.Message }
    await _candidateRepository.SaveAsync(failed, ct)
    _logger.LogWarning("Iteration {i}: evaluation exception — {msg}", i, ex.Message)
    UpdateRunManifest(runDir, i, currentBestCandidateId)
    continue

  // Step 4: Score and track best
  evaluated = candidate with { BestScore = evalResult.PassRate, TokenCost = evalResult.TotalTokenCost, Status = Evaluated }
  await _candidateRepository.SaveAsync(evaluated, ct)
  
  if IsBetter(evaluated, currentBestCandidate, config.ScoreImprovementThreshold):
    currentBestCandidate = evaluated

  // Step 5: Persist run state
  UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId)
  currentCandidate = evaluated   // next iteration proposes from latest evaluated

POST-LOOP:
  bestCandidate = await _candidateRepository.GetBestAsync(command.OptimizationRunId, ct)
  proposedDir = Path.Combine(runDir, "_proposed")
  WriteProposedSnapshot(proposedDir, bestCandidate)
  WriteSummaryMarkdown(runDir, allEvaluatedCandidates)
  
  return new OptimizationResult { ..., ProposedChangesPath = proposedDir }
```

#### Key private methods to implement

```csharp
/// <summary>
/// Loads all EvalTask JSON files from the configured path.
/// Returns empty list (not exception) if directory is missing.
/// </summary>
private async Task<IReadOnlyList<EvalTask>> LoadEvalTasksAsync(string path, CancellationToken ct);

/// <summary>
/// Reads run_manifest.json if present; returns a default manifest
/// (LastCompletedIteration = 0, BestCandidateId = null) if absent.
/// </summary>
private RunManifest LoadOrCreateRunManifest(string runDir);

/// <summary>
/// Atomically writes run_manifest.json using temp+rename.
/// Sets write_completed: true in the JSON.
/// </summary>
private void UpdateRunManifest(string runDir, int lastCompletedIteration, Guid? bestCandidateId);

/// <summary>
/// Returns true when candidate's pass rate exceeds currentBest by at least threshold,
/// OR when currentBest is null. Ties broken by token cost then iteration.
/// </summary>
private static bool IsBetter(HarnessCandidate candidate, HarnessCandidate? currentBest, double threshold);

/// <summary>
/// Writes each skill file from the candidate snapshot to {runDir}/candidates/{candidateId}/snapshot/.
/// </summary>
private static void WriteSnapshotFiles(string runDir, HarnessCandidate candidate);

/// <summary>
/// Copies best candidate snapshot files to {runDir}/_proposed/.
/// Overwrites if directory exists.
/// </summary>
private static void WriteProposedSnapshot(string proposedDir, HarnessCandidate? best);

/// <summary>
/// Writes summary.md to runDir. Table columns: Iteration | CandidateId | PassRate | TokenCost | Status | Changes.
/// </summary>
private async Task WriteSummaryMarkdownAsync(string runDir, Guid optimizationRunId, CancellationToken ct);

/// <summary>
/// Deletes the oldest optimization run directories when count exceeds maxRunsToKeep.
/// Skips the current run. No-op when maxRunsToKeep == 0.
/// </summary>
private static void EnforceRetentionPolicy(int maxRunsToKeep, string traceDirectoryRoot, Guid currentRunId);

/// <summary>
/// Resolves or creates the seed candidate:
/// - If command.SeedCandidateId is set, loads from repository
/// - Otherwise, calls ISnapshotBuilder to capture current active state
/// </summary>
private async Task<HarnessCandidate> ResolveSeedCandidateAsync(
    RunHarnessOptimizationCommand command,
    MetaHarnessConfig config,
    CancellationToken ct);
```

#### `run_manifest.json` schema

```json
{
  "optimizationRunId": "...",
  "lastCompletedIteration": 3,
  "bestCandidateId": "...",
  "startedAt": "2026-04-11T00:00:00Z",
  "write_completed": true
}
```

A `RunManifest` private record (or nested class) holds these fields for deserialization.

#### Best-candidate selection tie-breaking

Applied in `IsBetter` and again in `GetBestAsync` (repository). The handler's `IsBetter` tracks the in-memory current best during the loop; `GetBestAsync` is the authoritative source for post-loop selection:

1. Pass rate must exceed current best by at least `ScoreImprovementThreshold` to count as an improvement.
2. Among candidates with equal pass rate (within threshold): lower `TokenCost` wins.
3. Among candidates with equal pass rate and token cost: lower `Iteration` wins.

#### Failure handling

Both proposer failures (`HarnessProposalParsingException`) and evaluator exceptions:
- Create/update a `HarnessCandidate` with `Status = Failed` and `FailureReason` set
- Save to repository (so failures appear in summary)
- Log at `Warning` level with iteration number
- `continue` to the next iteration — failures count as completed iterations (they are not retried)

#### Retention policy

When `config.MaxRunsToKeep > 0`:
1. Enumerate directories under `{traceDirectoryRoot}/optimizations/`
2. Sort by creation time ascending (oldest first)
3. If count (excluding current run) exceeds `MaxRunsToKeep - 1`, delete oldest until count is within limit
4. Use `Directory.Delete(path, recursive: true)` — this is the only place in the harness where deletion occurs
5. Log each deletion at `Information` level

#### Cancellation

Check `ct.ThrowIfCancellationRequested()` at the top of each iteration (before proposer call). The handler does not swallow `OperationCanceledException` — it propagates to the caller.

---

## DI Registration

Register the handler automatically via MediatR assembly scanning (no manual registration needed if the assembly is already scanned). The validator must be registered with FluentValidation's assembly scanner in `Application.Core/DependencyInjection.cs` (or equivalent).

Confirm `Application.Core/DependencyInjection.cs` already scans the `Application.Core` assembly for both MediatR handlers and FluentValidation validators. If not, add:

```csharp
services.AddValidatorsFromAssemblyContaining<RunHarnessOptimizationCommandValidator>();
```

---

## Invariants and Edge Cases

- If `EvalTasksPath` directory is missing or empty: return a result indicating zero tasks (not an unhandled exception); log a warning.
- If the seed candidate cannot be built (e.g., skill directory missing): throw — this is a configuration error, not a recoverable failure.
- If `_proposed/` already exists from a previous run: overwrite it (the current run's best always wins).
- All JSON file writes use write-temp-then-rename for atomicity. The `run_manifest.json` write must set `"write_completed": true` only after all fields are written.
- The `summary.md` is written after the loop completes — it is not updated per iteration.
