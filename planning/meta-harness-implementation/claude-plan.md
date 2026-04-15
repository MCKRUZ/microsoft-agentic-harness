# Implementation Plan: Meta-Harness (v2 — post-review)

## What We Are Building

This plan implements the concepts from the paper "Meta-Harness: End-to-End Optimization of Model Harnesses" (arXiv:2603.28052) inside the Microsoft Agentic Harness project. The result is an automated harness engineering system: an outer loop that stores full execution traces from each agent run, then uses a coding-agent proposer — with grep/cat access to those traces — to propose improved harness configurations (skill files, system prompts, config values). The loop evaluates each proposed candidate against a user-defined task set, records scores, and writes the best candidate to disk for the user to review and promote.

The paper demonstrates that harness choice alone can cause a 6x performance gap on the same benchmark. Automated harness improvement requires rich trace history — not just scores — and a proposer that reads that history causally.

---

## Key Design Decisions (informed by external review)

**Three distinct identities, three scopes:**
- `OptimizationRunId` — the outer loop run (one per user-triggered `optimize` command)
- `CandidateId` — one proposed harness configuration per iteration
- `ExecutionRunId` — one agent run (one per eval task, or one for normal non-optimization agent runs)

These are never conflated. All filesystem paths encode all three where relevant.

**`ITraceWriter` is scoped, not singleton:** `StartRunAsync` on `IExecutionTraceStore` returns a `ITraceWriter` scoped to one `ExecutionRunId`. The writer owns its sequence counter and file lock. The store itself can be singleton. This prevents concurrent-write corruption in JSONL files.

**Atomic writes everywhere:** All JSON files are written to a temp path then atomically renamed. A `"write_completed": true` marker field in manifests lets readers detect incomplete writes.

**Secrets never reach disk:** An `ISecretRedactor` is applied to all content before persistence. Config snapshots exclude any key matching a configurable secrets denylist.

**Shell tool is opt-in and restricted:** `EnableShellTool` defaults to `false`. When enabled, only allowlisted read-only commands (`grep`, `rg`, `cat`, `find`, `ls`, `head`, `tail`, `jq`, `wc`) are accepted, with shell metacharacter validation.

**Candidate isolation via `ISkillContentProvider`:** The evaluator injects candidate skill content in-memory rather than writing to the active skill directory.

---

## Implementation Sequence

1. `MetaHarnessConfig` — must exist first; everything else reads it
2. `ISecretRedactor` — needed by trace store and snapshot builder
3. Execution Trace Infrastructure (`IExecutionTraceStore`, `ITraceWriter`, `FileSystemExecutionTraceStore`)
4. Causal OTel Span Attribution — extends existing telemetry
5. Agent History Store (`IAgentHistoryStore`, `JsonlAgentHistoryStore`)
6. `ReadHistoryTool` — depends on `IAgentHistoryStore`
7. SKILL.md Extension — parser changes
8. `ISkillContentProvider` + `CandidateSkillContentProvider` — needed by evaluator
9. Harness Candidate Management (`HarnessCandidate`, `IHarnessCandidateRepository`, `FileSystemHarnessCandidateRepository`)
10. `IHarnessProposer` + `OrchestratedHarnessProposer`
11. `IEvaluationService` + `AgentEvaluationService`
12. `RestrictedSearchTool` — proposer-only tool
13. MCP Trace Resources — `TraceResourceProvider`
14. `RunHarnessOptimizationCommand` + handler (outer loop)
15. Console UI `optimize` command

---

## Section 1: Config (`MetaHarnessConfig`)

**Where:** `Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs`

Nested under `AppConfig.MetaHarness`, bound via `IOptionsMonitor<MetaHarnessConfig>`.

Properties:

| Property | Type | Default | Purpose |
|---|---|---|---|
| `TraceDirectoryRoot` | string | `"traces"` | Root path for all trace output |
| `MaxIterations` | int | 10 | Iterations per optimization run |
| `SearchSetSize` | int | 50 | Max eval tasks per candidate |
| `ScoreImprovementThreshold` | double | 0.01 | Min pass-rate delta to count as improvement |
| `AutoPromoteOnImprovement` | bool | false | Auto-apply best candidate; false = write to disk only |
| `EvalTasksPath` | string | `"eval-tasks"` | Path to eval task JSON files |
| `SeedCandidatePath` | string | `""` | Optional path to seed harness snapshot |
| `MaxEvalParallelism` | int | 1 | Controlled parallelism for eval tasks (1 = sequential) |
| `EvaluationTemperature` | double | 0.0 | LLM temperature for deterministic eval |
| `EvaluationModelVersion` | string? | null | Optional model override for eval (null = use default) |
| `SnapshotConfigKeys` | IReadOnlyList\<string\> | `[]` | AppConfig keys to include in harness snapshot |
| `SecretsRedactionPatterns` | IReadOnlyList\<string\> | `["Key","Secret","Token","Password","ConnectionString"]` | Config key substrings that are never snapshotted |
| `MaxFullPayloadKB` | int | 512 | Max size for per-call full payload artifacts |
| `MaxRunsToKeep` | int | 20 | How many optimization runs to retain (0 = unlimited) |
| `EnableShellTool` | bool | false | Opt-in: allow proposer to run restricted shell commands |
| `EnableMcpTraceResources` | bool | true | Expose traces via MCP resources |

---

## Section 2: Secret Redaction

**Where:** `Application.AI.Common/Interfaces/ISecretRedactor.cs` (interface), `Infrastructure.AI/Security/PatternSecretRedactor.cs` (implementation)

`ISecretRedactor` has a single method: `Redact(string input) → string`. The implementation replaces occurrences of known secrets patterns with `"[REDACTED]"`. It uses:
- The configured `SecretsRedactionPatterns` list to filter config snapshot keys
- A regex scan of free-text content for common secret-like strings (Bearer tokens, connection strings)

Register as singleton in `Infrastructure.AI/DependencyInjection.cs`.

---

## Section 3: Execution Trace Persistence Infrastructure

### Identity Model and Directory Structure

```
{trace_root}/
  optimizations/
    {optimizationRunId}/
      run_manifest.json        # status, lastCompletedIteration, bestCandidateId
      candidates/
        index.jsonl            # One record per candidate: {candidateId, passRate, tokenCost, status, iteration}
        {candidateId}/
          candidate.json       # HarnessCandidate snapshot (write_completed: true)
          snapshot/            # Skill file content + system prompt
          eval/
            {taskId}/
              {executionRunId}/  # Per-task eval run (one per eval task per iteration)
                manifest.json  # {executionRunId, candidateId, iteration, taskId, write_completed}
                scores.json
                traces.jsonl
                decisions.jsonl
                turns/{n}/
                  system_prompt.md
                  tool_calls.jsonl
                  model_response.md
                  state_snapshot.json
                  tool_results/
                    {callId}.json  # Full payload (may be large)
  executions/
    {executionRunId}/            # Normal (non-optimization) agent runs
      manifest.json
      traces.jsonl
      decisions.jsonl
      turns/{n}/...
```

### `TraceScope` Value Object

**Where:** `Domain.Common/MetaHarness/TraceScope.cs`

Record with: `ExecutionRunId` (Guid, always required), `OptimizationRunId` (Guid?), `CandidateId` (Guid?), `TaskId` (string?). Determines which directory subtree traces are written to. A static factory method `TraceScope.ForExecution(executionRunId)` creates a standalone (non-optimization) scope.

### `ITraceWriter` Interface

**Where:** `Application.AI.Common/Interfaces/Traces/ITraceWriter.cs`

Returned by `IExecutionTraceStore.StartRunAsync`. Scoped to one `ExecutionRunId`. Methods:

- `WriteTurnAsync(turnNumber, TurnArtifacts)` — writes `turns/{n}/` subdirectory (system prompt, tool calls, model response, state snapshot)
- `AppendTraceAsync(ExecutionTraceRecord)` — appends one JSONL record to `traces.jsonl`; thread-safe via internal `SemaphoreSlim`; sequence numbers via `Interlocked.Increment` on a per-writer counter
- `WriteScoresAsync(HarnessScores)` — atomic write of `scores.json`
- `WriteSummaryAsync(string markdown)` — atomic write of `summary.md`
- `CompleteAsync()` — finalizes the run; writes `"write_completed": true` to manifest

The writer stores the resolved absolute run directory so callers never need to compute paths.

### `IExecutionTraceStore` Interface

**Where:** `Application.AI.Common/Interfaces/Traces/IExecutionTraceStore.cs`

- `StartRunAsync(TraceScope scope, RunMetadata metadata, CancellationToken)` → `ITraceWriter` — creates directory structure, writes initial `manifest.json`
- `GetRunDirectoryAsync(TraceScope scope)` → `string` — returns absolute directory path for proposer filesystem access

### `FileSystemExecutionTraceStore` Implementation

**Where:** `Infrastructure.AI/Traces/FileSystemExecutionTraceStore.cs`

- Resolves directory from `TraceScope`: if `OptimizationRunId` is set, path is under `optimizations/`; otherwise under `executions/`
- All JSON writes use write-temp-then-rename (atomic)
- `ISecretRedactor` applied to all string content before writing
- Full tool call payloads written to `turns/{n}/tool_results/{callId}.json` if size ≤ `MaxFullPayloadKB`; inline `payload_summary` truncated to 500 chars
- Register as singleton in `Infrastructure.AI/DependencyInjection.cs`

### `ExecutionTraceRecord` Schema (JSONL per line)

| Field | Type | Description |
|---|---|---|
| `seq` | long | Monotonically increasing per writer instance |
| `ts` | ISO 8601 | Timestamp |
| `type` | string | `tool_call`, `tool_result`, `decision`, `observation` |
| `execution_run_id` | string | Correlation |
| `candidate_id` | string? | Set for optimization eval runs |
| `iteration` | int? | Set for optimization eval runs |
| `task_id` | string? | Set for eval task runs |
| `turn_id` | string | Turn identifier |
| `tool_name` | string? | For tool events |
| `result_category` | string? | `success`, `partial`, `error`, `timeout`, `blocked` |
| `payload_summary` | string? | ≤500 chars truncated |
| `payload_full_path` | string? | Relative path to full artifact file |
| `redacted` | bool? | True if content was redacted |

### Wiring into the Agent Pipeline

**`AgentExecutionContextFactory`:** Add `IExecutionTraceStore` as a constructor parameter. On context creation, call `StartRunAsync` with a new `TraceScope` (from an optional `TraceScope` parameter if building an eval context; otherwise `TraceScope.ForExecution(newGuid)`). Store the returned `ITraceWriter` on the context.

**`ToolDiagnosticsMiddleware`:** After each tool call, call `context.TraceWriter.AppendTraceAsync(...)` with a `tool_result` record. Apply `ISecretRedactor` to the result before writing.

---

## Section 4: Causal Span Attribution in OpenTelemetry

**Where:** Existing span processor in `Infrastructure.Observability/`

Follow OTel GenAI semantic conventions: span name `execute_tool {tool_name}`, `gen_ai.operation.name = "execute_tool"`, `gen_ai.tool.name`, `gen_ai.tool.call.id`.

Add custom causal attributes:
- `tool.input_hash` — SHA256 of serialized input (guard with `IsAllDataRequested`)
- `tool.result_category` — same bucketed outcome as trace records
- `gen_ai.harness.candidate_id` — from `IAgentExecutionContext.TraceScope.CandidateId`
- `gen_ai.harness.iteration` — from `IAgentExecutionContext.TraceScope` when set

No new activity sources needed — extend the existing processor.

---

## Section 5: Queryable Agent History

### `IAgentHistoryStore` Interface

**Where:** `Application.AI.Common/Interfaces/Memory/IAgentHistoryStore.cs`

- `AppendAsync(AgentDecisionEvent, CancellationToken)`
- `QueryAsync(DecisionLogQuery, CancellationToken)` → `IAsyncEnumerable<AgentDecisionEvent>`

`AgentDecisionEvent` is an immutable record: `Sequence` (long), `Timestamp` (DateTimeOffset), `EventType` (string), `ExecutionRunId` (string), `TurnId` (string), optional `ToolName`, `ResultCategory`, `Payload` (JsonElement?).

`DecisionLogQuery` filters by `ExecutionRunId` (required), optional `TurnId`, `EventType`, `ToolName`, `Since` (sequence number).

### `JsonlAgentHistoryStore` Implementation

**Where:** `Infrastructure.AI/Memory/JsonlAgentHistoryStore.cs`

Writes to `decisions.jsonl` in the trace run directory (path resolved from `ITraceWriter`). For `QueryAsync`, streams line-by-line, applies predicates, skips records ≤ `Since`. Thread-safe append via the `ITraceWriter`'s per-file `SemaphoreSlim` (share the lock with `traces.jsonl` since they're in the same directory).

### `ReadHistoryTool`

**Where:** `Infrastructure.AI/Tools/ReadHistoryTool.cs`

Keyed as `"read_history"`. Schema: `execution_run_id` (required), optional `event_type`, `tool_name`, `since` (int), `limit` (int, default 100). Returns results as JSON array. Register with keyed DI.

---

## Section 6: SKILL.md Extension

Add `## Objectives` and `## Trace Format` as first-class `SkillSection` types in the skill parser. `Objectives` surfaces what the agent optimizes for (success criteria, failure patterns, trade-offs). `TraceFormat` documents the trace directory layout for the proposer's navigation.

Both sections are optional — skills without them continue to work. Update `ISkillMetadataRegistry` to include them in returned `SkillDefinition`. Update `skills/research-agent/SKILL.md` as the reference implementation. Create `skills/harness-proposer/SKILL.md` with both sections filled in — the proposer's skill file is itself a candidate for optimization.

---

## Section 7: Candidate Isolation (`ISkillContentProvider`)

### Interface

**Where:** `Application.AI.Common/Interfaces/Skills/ISkillContentProvider.cs`

Single method: `GetSkillContentAsync(skillPath, CancellationToken)` → `string?`

### Implementations

- `FileSystemSkillContentProvider` — reads from disk (default for normal agent runs)
- `CandidateSkillContentProvider` — reads from `HarnessCandidate.SkillFileSnapshots` (in-memory dictionary); returns null for paths not in the snapshot, falling back to the filesystem implementation

`AgentExecutionContextFactory` accepts an optional `ISkillContentProvider` override. `AgentEvaluationService` constructs a `CandidateSkillContentProvider` for each evaluation and passes it to the factory.

---

## Section 8: Harness Candidate Management

### `HarnessSnapshot` Value Object

**Where:** `Domain.Common/MetaHarness/HarnessSnapshot.cs`

Immutable record representing a deterministic point-in-time harness configuration:
- `SkillFileSnapshots` — `IReadOnlyDictionary<string, string>` (skill path → content, only the active agent's skill directory, not all skills)
- `SystemPromptSnapshot` — string (redacted via `ISecretRedactor`)
- `ConfigSnapshot` — `IReadOnlyDictionary<string, string>` (only keys in `MetaHarnessConfig.SnapshotConfigKeys`, never keys matching secrets denylist)
- `SnapshotManifest` — `IReadOnlyList<SnapshotEntry>` where `SnapshotEntry` = `{Path, Sha256Hash}` for reproducibility verification

### `HarnessCandidate` Domain Model

**Where:** `Domain.Common/MetaHarness/HarnessCandidate.cs`

Immutable record:

| Property | Type | Description |
|---|---|---|
| `CandidateId` | Guid | Unique identifier |
| `OptimizationRunId` | Guid | Outer loop run this belongs to |
| `ParentCandidateId` | Guid? | Ancestor candidate; null for seed |
| `Iteration` | int | Iteration number |
| `CreatedAt` | DateTimeOffset | |
| `Snapshot` | HarnessSnapshot | The proposed configuration |
| `BestScore` | double? | Pass rate after evaluation |
| `TokenCost` | long? | Cumulative tokens during evaluation |
| `Status` | HarnessCandidateStatus | `Proposed`, `Evaluated`, `Failed`, `Promoted` |
| `FailureReason` | string? | Populated when `Status == Failed` |

`HarnessCandidateStatus` is an enum in the same namespace.

### `IHarnessCandidateRepository` Interface

**Where:** `Application.AI.Common/Interfaces/MetaHarness/IHarnessCandidateRepository.cs`

- `SaveAsync(candidate, ct)`
- `GetAsync(candidateId, ct)` → `HarnessCandidate?`
- `GetLineageAsync(candidateId, ct)` → `IReadOnlyList<HarnessCandidate>` (ancestor chain, oldest first)
- `GetBestAsync(optimizationRunId, ct)` → `HarnessCandidate?`
- `ListAsync(optimizationRunId, ct)` → `IReadOnlyList<HarnessCandidate>`

### `FileSystemHarnessCandidateRepository` Implementation

**Where:** `Infrastructure.AI/MetaHarness/FileSystemHarnessCandidateRepository.cs`

Stores each candidate as `{trace_root}/optimizations/{optRunId}/candidates/{candidateId}/candidate.json`. Writes atomically (temp + rename); sets `write_completed: true` last.

Maintains `{...}/candidates/index.jsonl` — one record per candidate: `{candidateId, passRate, tokenCost, status, iteration}`. Updated atomically after each `SaveAsync`. `GetBestAsync` reads only this index file (O(1) scan of a small file), filters to `status == Evaluated`, applies tie-breaking: (1) highest pass rate by at least `ScoreImprovementThreshold`, (2) lowest token cost, (3) lowest iteration.

---

## Section 9: Meta-Harness Outer Optimization Loop

### 9a: Proposer Interface and Implementation

**Interface:** `Application.AI.Common/Interfaces/MetaHarness/IHarnessProposer.cs`

`ProposeAsync(HarnessProposerContext, CancellationToken)` → `HarnessProposal`

`HarnessProposerContext`: current candidate (full `HarnessCandidate`), optimization run directory path, prior candidate IDs (for filesystem navigation), iteration number.

`HarnessProposal`: `ProposedSkillChanges` (dict: skill path → content), `ProposedConfigChanges` (dict: config key → value), `ProposedSystemPromptChange` (string?), `Reasoning` (string).

**Implementation:** `Infrastructure.AI/MetaHarness/OrchestratedHarnessProposer.cs`

Delegates to `RunOrchestratedTaskCommandHandler` via `IMediator`. Task: "You are a harness engineer. Read execution traces in {runDirectory}, review prior candidates, and propose an improved harness." Proposer's tool set: `FileSystemService` (sandboxed to optimization run directory), `ReadHistoryTool`, `RestrictedSearchTool`, MCP trace resources.

Proposer output is extracted as a JSON block: find first `{`, last `}`, parse with `JsonDocument.Parse`. On parse failure: throw `HarnessProposalParsingException` with the raw output (caller marks candidate `Failed`).

Proposer's own skill file: `skills/harness-proposer/SKILL.md`. This skill file is itself part of the harness and therefore a candidate for optimization.

### 9b: Evaluation Service

**Interface:** `Application.AI.Common/Interfaces/MetaHarness/IEvaluationService.cs`

`EvaluateAsync(candidate, evalTasks, CancellationToken)` → `EvaluationResult`

`EvalTask`: `TaskId`, `Description`, `InputPrompt`, `ExpectedOutputPattern` (optional regex), `Tags`.

`EvaluationResult`: `CandidateId`, `PassRate` (double), `TotalTokenCost` (long), `PerExampleResults` (list of `{TaskId, Passed, TokenCost}`).

**Implementation:** `Infrastructure.AI/MetaHarness/AgentEvaluationService.cs`

For each eval task:
1. Construct a `TraceScope` with `OptimizationRunId`, `CandidateId`, `TaskId`, new `ExecutionRunId`
2. Construct a `CandidateSkillContentProvider` from the candidate's snapshot
3. Build an `IAgentExecutionContext` via factory with the override skill provider and trace scope
4. Run the agent on `task.InputPrompt`; collect output and token count
5. Grade against `ExpectedOutputPattern`: compile with `Regex.Match(output, pattern, RegexOptions.None, TimeSpan.FromSeconds(5))`. Timeout = failed (not error).

Task parallelism controlled by `MetaHarnessConfig.MaxEvalParallelism`. Each task run writes to its own trace directory under the candidate.

Eval task files at `MetaHarnessConfig.EvalTasksPath`, one JSON file per task.

### 9c: `RestrictedSearchTool`

**Where:** `Infrastructure.AI/Tools/RestrictedSearchTool.cs`

Keyed as `"restricted_search"`. Schema: `command` (string), `working_directory` (string, optional, defaults to optimization run directory).

Before execution:
1. Parse `command` to extract binary name (first whitespace-delimited token)
2. Reject if binary not in allowlist: `grep`, `rg`, `cat`, `find`, `ls`, `head`, `tail`, `jq`, `wc`
3. Reject if `command` contains shell metacharacters: `;`, `|`, `&&`, `||`, `>`, `<`, `` ` ``, `$(`, `\n`
4. Resolve `working_directory` via `Path.GetFullPath()` and verify it is under `TraceDirectoryRoot`; reject symlinks pointing outside
5. Execute via `Process.Start` with `UseShellExecute = false`, no inherited env, explicit `WorkingDirectory`
6. Timeout 30s; output size cap 1 MB

Only included in proposer tool set when `MetaHarnessConfig.EnableShellTool == true`. Off by default.

### 9d: MCP Trace Resources

**Where:** `Infrastructure.AI.MCP/Resources/TraceResourceProvider.cs`

Exposes `trace://{optimizationRunId}/{relativePath}` resources. List operation returns files in the run directory. Read operation returns file content.

Before serving: validate JWT auth (existing middleware), resolve path via `Path.GetFullPath()`, reject any `..` segments, reject symlinks outside the trace root, reject paths outside `{trace_root}/optimizations/{optimizationRunId}/`.

Gate behind `MetaHarnessConfig.EnableMcpTraceResources`.

### 9e: Optimization Command and Handler

**Command:** `Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs`

Fields: `OptimizationRunId` (Guid), `SeedCandidateId` (Guid?, optional — resumes from prior candidate), `MaxIterations` (int?, overrides config). Validated with FluentValidation: `OptimizationRunId` must not be empty.

**Handler:** `Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs`

```
SETUP:
  Load eval tasks from EvalTasksPath
  Load or create seed candidate (from active skill/prompt/config snapshot via ISnapshotBuilder)
  Initialize/resume from run_manifest.json (if exists and has lastCompletedIteration)
  Enforce MaxRunsToKeep retention: delete oldest optimization run directories if count exceeds limit

LOOP for iteration 1..MaxIterations (skip if already completed per run_manifest.json):
  1. Invoke IHarnessProposer.ProposeAsync → HarnessProposal
     - On HarnessProposalParsingException: create candidate with Status=Failed, FailureReason, save, write failure trace, continue
  2. Create HarnessCandidate from proposal with Status=Proposed, save
  3. Write candidate snapshot files to {candidateId}/snapshot/
  4. Invoke IEvaluationService.EvaluateAsync → EvaluationResult
     - On exception: update candidate Status=Failed, FailureReason=exception.Message, save, continue
  5. Update candidate with BestScore, TokenCost, Status=Evaluated, save
  6. Update candidates/index.jsonl
  7. Update run_manifest.json with lastCompletedIteration=i, bestCandidateId
  8. Honor CancellationToken: check between iterations

POST-LOOP:
  Best candidate = IHarnessCandidateRepository.GetBestAsync(optimizationRunId)
  Write best candidate's snapshot to {trace_root}/optimizations/{optRunId}/_proposed/
  Write summary.md comparing all evaluated candidates (tabular: iteration, pass rate, token cost, key changes)
  Return OptimizationResult { OptimizationRunId, BestCandidateId, BestScore, IterationCount, ProposedChangesPath }
```

**Best candidate selection tie-breaking:** higher pass rate → lower token cost → earlier iteration. Pass rate must exceed current best by at least `ScoreImprovementThreshold` to be considered an improvement.

### 9f: `ISnapshotBuilder`

**Where:** `Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs`

Takes the current active skill directory, system prompt, and allowlisted config keys → produces a `HarnessSnapshot`. Applies `ISecretRedactor` to all content. Computes SHA256 hashes for `SnapshotManifest`.

Implementation: `Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs`

---

## Section 10: Console UI Entry Point

**Where:** `Presentation.ConsoleUI/`

New `optimize` command:
1. Prompt user: optimization run description, optional max iterations override
2. Generate `OptimizationRunId` (Guid)
3. Dispatch `RunHarnessOptimizationCommand` via `IMediator`
4. Per-iteration progress: `Iteration {i}/{max} | Score: {score:.2%} | Δ{delta:+.2%} | Tokens: {tokens} | {changeSummary}`
5. On completion: print path to `_proposed/` and instructions for reviewing/promoting

---

## Testing Plan

### Unit Tests (`Application.Core.Tests`)

`RunHarnessOptimizationCommandHandlerTests`:
- `Handle_ExecutesAllIterations_WhenAllSucceed`
- `Handle_ContinuesAfterProposerParsingFailure_MarksFailedAndContinues`
- `Handle_ContinuesAfterEvalException_MarksFailedAndContinues`
- `Handle_AppliesScoreThreshold_WhenImprovementBelowThreshold`
- `Handle_TieBreaksCorrectly_LowerTokenCostWins`
- `Handle_ResumesFromManifest_SkipsCompletedIterations`
- `Handle_WritesProposedChangesToOutputDir`

`HarnessCandidateTests`:
- `StatusTransition_WithExpression_ProducesNewRecord`
- `Snapshot_ContainsNoSecretKeys`

### Integration Tests (`Infrastructure.AI.Tests`)

`FileSystemExecutionTraceStoreTests`:
- `StartRunAsync_CreatesManifestWithWriteCompleted`
- `WriteTurnAsync_CreatesSubdirectoryAndFiles`
- `AppendTraceAsync_WritesJsonlLine_Threadsafe` (10 concurrent writers, no corruption)
- `AtomicWrite_ReaderNeverSeesPartialJson` (simulate interrupted write)

`JsonlAgentHistoryStoreTests`:
- `AppendAsync_WritesRecord`
- `QueryAsync_WithSince_SkipsEarlierRecords`

`FileSystemHarnessCandidateRepositoryTests`:
- `SaveAndGet_RoundTrips`
- `GetLineageAsync_ReturnsFull_AncestorChain`
- `GetBestAsync_ReadsIndexOnly_ReturnsHighestScoredEvaluated`
- `SaveAsync_UpdatesIndexAtomically`

`RestrictedSearchToolTests`:
- `Execute_AllowsGrep_WithinTraceRoot`
- `Execute_Rejects_NonAllowlistedBinary`
- `Execute_Rejects_ShellMetacharacters`
- `Execute_Rejects_PathTraversal`

`OptimizationLoopIntegrationTest`:
- `FullLoop_WithScriptedProposer_ProducesExpectedDirectoryStructure` (temp dir, canned proposer returning valid/invalid proposals, verify `_proposed/` output)

`TraceResourceProviderTests`:
- `Read_RejectsPathTraversal`
- `Read_RequiresAuth`

---

## New Files Summary

```
Domain.Common/
  Config/MetaHarness/MetaHarnessConfig.cs
  MetaHarness/TraceScope.cs
  MetaHarness/HarnessSnapshot.cs
  MetaHarness/SnapshotEntry.cs
  MetaHarness/HarnessCandidate.cs
  MetaHarness/HarnessCandidateStatus.cs
  MetaHarness/EvalTask.cs

Application.AI.Common/
  Interfaces/ISecretRedactor.cs
  Interfaces/Traces/IExecutionTraceStore.cs
  Interfaces/Traces/ITraceWriter.cs
  Interfaces/Memory/IAgentHistoryStore.cs
  Interfaces/Skills/ISkillContentProvider.cs
  Interfaces/MetaHarness/IHarnessProposer.cs
  Interfaces/MetaHarness/IEvaluationService.cs
  Interfaces/MetaHarness/IHarnessCandidateRepository.cs
  Interfaces/MetaHarness/ISnapshotBuilder.cs

Application.Core/
  CQRS/MetaHarness/RunHarnessOptimizationCommand.cs
  CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs
  CQRS/MetaHarness/RunHarnessOptimizationCommandValidator.cs

Infrastructure.AI/
  Security/PatternSecretRedactor.cs
  Traces/FileSystemExecutionTraceStore.cs
  Memory/JsonlAgentHistoryStore.cs
  Skills/FileSystemSkillContentProvider.cs
  Skills/CandidateSkillContentProvider.cs
  MetaHarness/OrchestratedHarnessProposer.cs
  MetaHarness/AgentEvaluationService.cs
  MetaHarness/FileSystemHarnessCandidateRepository.cs
  MetaHarness/ActiveConfigSnapshotBuilder.cs
  Tools/ReadHistoryTool.cs
  Tools/RestrictedSearchTool.cs

Infrastructure.AI.MCP/
  Resources/TraceResourceProvider.cs

skills/harness-proposer/SKILL.md

Tests/Application.AI.Common.Tests/
  MetaHarness/HarnessCandidateTests.cs

Tests/Application.Core.Tests/
  CQRS/MetaHarness/RunHarnessOptimizationCommandHandlerTests.cs

Tests/Infrastructure.AI.Tests/
  Traces/FileSystemExecutionTraceStoreTests.cs
  Memory/JsonlAgentHistoryStoreTests.cs
  MetaHarness/FileSystemHarnessCandidateRepositoryTests.cs
  MetaHarness/OptimizationLoopIntegrationTests.cs
  Tools/RestrictedSearchToolTests.cs
  MCP/TraceResourceProviderTests.cs
```

**Modified files:**
- `Domain.Common/Config/AppConfig.cs` — add `MetaHarness` property
- `Application.AI.Common/` — extend `SkillSection` enum, update skill parser, update `ISkillMetadataRegistry`
- `Application.AI.Common/Factories/AgentExecutionContextFactory.cs` — inject `IExecutionTraceStore` + optional `TraceScope` + optional `ISkillContentProvider`; start run on context creation
- `Application.AI.Common/Middleware/ToolDiagnosticsMiddleware.cs` — append trace records + apply redaction
- `Infrastructure.AI/DependencyInjection.cs` — register all new services and tools
- `Infrastructure.AI.MCP/DependencyInjection.cs` — register `TraceResourceProvider`
- `Infrastructure.Observability/` — extend span processor with causal attributes
- `Presentation.ConsoleUI/` — add `optimize` command
- `appsettings.json` — add `"MetaHarness"` section
- `skills/research-agent/SKILL.md` — add `## Objectives` and `## Trace Format` sections
