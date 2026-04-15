# Integration Notes: OpenAI Review Feedback

Reviewer: gpt-5.2 | Date: 2026-04-11

---

## Integrating

### #1 — Run ID scoping confusion → INTEGRATE
The reviewer is correct. `RunId` is used for two distinct concepts: the optimization run (outer loop) and the agent execution run (per turn). This will cause silent directory collisions.

**Fix:** Rename throughout:
- `OptimizationRunId` (Guid) — the outer optimization loop, directory: `{trace_root}/optimizations/{optimizationRunId}/`
- `ExecutionRunId` (Guid) — one agent turn/task execution, directory: `{...}/executions/{executionRunId}/`
- Candidate directory: `{...}/candidates/{candidateId}/`
- Eval runs: `{...}/candidates/{candidateId}/eval/{taskId}/{executionRunId}/`

Update `IExecutionTraceStore` to accept a `TraceScope` record (OptimizationRunId?, CandidateId?, ExecutionRunId, TaskId?) so all write operations carry their full context.

### #2 — Eval trace placement underspecified → INTEGRATE
`AgentEvaluationService` must write traces under the candidate directory, not a free-floating execution directory. `manifest.json` for eval runs must include `candidateId`, `iteration`, `taskId`.

**Fix:** `AgentExecutionContextFactory` must accept an optional `TraceScope` override at context construction time. `AgentEvaluationService` constructs a `TraceScope` for each eval task and passes it into the context factory.

### #3 — Concurrent writes / singleton store → INTEGRATE
A singleton `IExecutionTraceStore` sharing a single `seq` counter across concurrent runs will corrupt JSONL.

**Fix:** 
- `StartRunAsync` returns a `ITraceWriter` (scoped object per execution run) that owns its own `SemaphoreSlim` and sequence counter via `Interlocked.Increment`
- Drop singleton registration; register store as transient (or keep singleton but have it return scoped writers)
- Per-file lock keyed by absolute path

### #4 — Truncation destroys evidence → INTEGRATE
Truncating tool payloads inline destroys stack traces. Store both summary and full artifact.

**Fix:** `traces.jsonl` records have `payload_summary` (inline, ≤500 chars) and `payload_full_path` (relative path to `turns/{n}/tool_results/{callId}.json`). Full artifacts written separately. Add `MaxFullPayloadKB` config.

### #5 — Non-atomic writes → INTEGRATE
Overwriting JSON files in-place risks partial reads by the proposer.

**Fix:** All JSON writes use write-to-temp + atomic rename pattern. Add `"write_completed": true` field to `manifest.json` and `candidate.json` that is written last (so an incomplete write is detectable).

### #6 — Budget decrement ambiguity → INTEGRATE
Remove the "decrement remaining budget" language. Clarify: failures count as an iteration. The loop runs for exactly `MaxIterations` iterations regardless of success/failure mix.

### #7 — Shell tool RCE risk → INTEGRATE (stronger mitigation)
The reviewer is right that working directory restriction alone is not enough — the LLM can run `python`, `curl`, `bash -c "..."`, etc.

**Fix:**
- Default `EnableShellTool = false` (must be explicitly opted in)
- Replace unrestricted execution with an **allowlisted command runner**: whitelist `grep`, `rg`, `cat`, `find`, `ls`, `head`, `tail`, `jq`, `wc`
- Parse `command` string: extract the binary name, reject anything not on the allowlist; reject strings containing `;`, `|`, `&&`, `||`, `>`, `<`, backtick, `$(`, `\n`
- Run via `Process.Start` with no shell (`UseShellExecute = false`), no inherited environment, timeout 30s, output size cap 1MB
- Rename to `RestrictedSearchTool` to make intent clear

### #8 — MCP trace resource auth → INTEGRATE
MCP exposure of traces without auth is a data leak risk.

**Fix:** `TraceResourceProvider` checks the existing JWT auth middleware before serving resources. Add path traversal protection: reject `..` in resource paths; resolve real paths; reject symlinks.

### #9 — Secrets in traces → INTEGRATE
System prompts and config snapshots may contain API keys and connection strings.

**Fix:** 
- Add `ISecretRedactor` interface in `Application.AI.Common/Interfaces/` with `Redact(string)` → `string`
- Default implementation uses a configurable regex denylist (config keys containing `Key`, `Secret`, `Token`, `Password`, `ConnectionString`)
- Apply to: system prompt snapshot, config snapshot, tool call payloads before writing
- Add `"redacted": true` field to trace records where content was redacted

### #10 — Regex grading ReDoS → INTEGRATE
Simple fix.

**Fix:** Compile eval task `ExpectedOutputPattern` with `Regex.Match(..., TimeSpan.FromSeconds(5))`. Mark task as failed (not error) if timeout exceeded; log warning.

### #12 — Sequential eval → INTEGRATE (partial)
Add `MaxEvalParallelism` to `MetaHarnessConfig` (default 1 = sequential for safety). Allow controlled parallelism with each task getting its own `TraceScope`.

### #13 — O(n) candidate scan → INTEGRATE
`GetBestAsync` loading all candidate files is O(n) file reads.

**Fix:** Maintain `{optimizations}/{optRunId}/candidates/index.jsonl` — one record per candidate (candidateId, score, tokenCost, status, iteration). Updated atomically after each evaluation. `GetBestAsync` reads only this index file.

### #15 — Snapshot scope unclear → INTEGRATE
Document what goes into a harness snapshot.

**Fix:** Define `HarnessSnapshot` value object with: skill file paths + contents (agent's skill directory only, not all skills), system prompt text, `SnapshotManifest` (list of `{path, sha256}` for reproducibility verification), allowlisted config keys (defined in `MetaHarnessConfig.SnapshotConfigKeys`). Never snapshot keys matching the secrets denylist.

### #16 — Candidate isolation mechanism → INTEGRATE
How does `AgentEvaluationService` inject candidate skill content without writing to the active skill directory?

**Fix:** Add `ISkillContentProvider` to `Application.AI.Common/Interfaces/`. Default `FileSystemSkillContentProvider` reads from disk. `CandidateSkillContentProvider` reads from `HarnessCandidate.SkillFileSnapshots` (in-memory). `AgentEvaluationService` constructs a `CandidateSkillContentProvider` and passes it to the agent context factory for isolated evaluation.

### #17 — Proposer output parsing fragility → INTEGRATE
Delimited sections are fragile.

**Fix:** Proposer output must be a JSON block. Extraction: find first `{`, last `}`, validate with `JsonDocument.Parse`. If parsing fails: mark candidate `Failed` with `FailureReason = "ProposerOutputParsingFailed: {truncated_output}"`, feed error back to proposer in next iteration's context.

### #18 — Reproducibility controls → INTEGRATE (partial)
Full statistical CI is overkill for POC. But add basic determinism knobs.

**Fix:** Add `EvaluationTemperature` (default 0.0) and `EvaluationModelVersion` (optional override) to `MetaHarnessConfig`. These are applied when constructing the evaluation agent context.

### #19 — Threshold and tie-breaking → INTEGRATE
Apply threshold to "improvement" decision. Add tie-breaking rules.

**Fix:** Best candidate selection: (1) pass rate must exceed current best by at least `ScoreImprovementThreshold`; (2) if equal pass rate, prefer lower token cost; (3) if both equal, prefer earlier iteration.

### #20 — Resumability → INTEGRATE
Long runs must be resumable.

**Fix:** Write `run_manifest.json` at `{optimizations}/{optRunId}/run_manifest.json` after each iteration with: `status`, `lastCompletedIteration`, `bestCandidateId`. `RunHarnessOptimizationCommandHandler` checks this at startup: if iteration `i` already has a candidate with status `Evaluated`, skip re-evaluation. Honor `CancellationToken` in all inner loops.

### #21 — Path traversal via symlinks → INTEGRATE
Working directory validation must resolve real paths first.

**Fix:** Before validating that a path is under `TraceDirectoryRoot`, call `Path.GetFullPath()` to resolve symlinks and `..`. Reject resolved paths outside the root.

### #22 — Retention/cleanup → INTEGRATE (partial)
Add to `MetaHarnessConfig`: `MaxRunsToKeep` (default 20, 0 = unlimited). Cleanup of old runs happens at the start of each new optimization run.

### #23 — Concurrency and atomicity tests → INTEGRATE
Add to `Infrastructure.AI.Tests`:
- Concurrent `AppendTraceAsync` from 10 parallel tasks: verify no JSONL corruption
- Atomic write: simulate crash mid-write; verify reader never sees partial `candidate.json`
- Shell tool rejects `..` in arguments, shell metacharacters, non-allowlisted commands

### #24 — Proposer parsing failure tests → INTEGRATE
Add to `Application.Core.Tests`: proposer returns invalid JSON → candidate status `Failed` → loop continues.

---

## Not Integrating

### #11 — File explosion concern
The concern is valid but the mitigation (bundling turn artifacts) would complicate the grep-friendly structure that is core to the Meta-Harness design. Retention policy (from #22) addresses the disk usage problem sufficiently for a POC.

### #14 — Domain config placement
Reviewer flags `Domain.Common/Config/` as wrong layer. This is intentional project convention — every config class in this codebase lives in `Domain.Common/Config/`. Changing it would require refactoring all other config classes and is out of scope for this feature.
