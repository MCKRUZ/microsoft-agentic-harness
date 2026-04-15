<!-- PROJECT_CONFIG
runtime: dotnet
test_command: dotnet test src/AgenticHarness.slnx
END_PROJECT_CONFIG -->

<!-- SECTION_MANIFEST
section-01-config
section-02-secret-redaction
section-03-trace-domain
section-04-trace-infrastructure
section-05-otel-spans
section-06-history-store
section-07-skill-extension
section-08-skill-provider
section-09-candidate-domain
section-10-candidate-repository
section-11-proposer
section-12-evaluator
section-13-tools
section-14-outer-loop
section-15-console-ui
END_MANIFEST -->

# Implementation Sections Index

## Dependency Graph

| Section | Depends On | Blocks | Parallelizable With |
|---|---|---|---|
| section-01-config | — | 03, 04, 09, 13 | section-02 |
| section-02-secret-redaction | — | 04, 09 | section-01, section-07, section-08 |
| section-03-trace-domain | 01 | 04 | — |
| section-04-trace-infrastructure | 01, 02, 03 | 05, 06, 10, 11, 12, 13 | — |
| section-05-otel-spans | 04 | — | section-06 |
| section-06-history-store | 04 | 11 | section-05, section-07, section-08 |
| section-07-skill-extension | — | 11 | section-02, section-05, section-06, section-08 |
| section-08-skill-provider | — | 11, 12 | section-07 |
| section-09-candidate-domain | 01, 02 | 10, 11, 12, 14 | — |
| section-10-candidate-repository | 04, 09 | 11, 12, 14 | — |
| section-11-proposer | 04, 06, 07, 08, 09, 10 | 14 | section-12 |
| section-12-evaluator | 04, 08, 09, 10 | 14 | section-11 |
| section-13-tools | 01, 04 | 14 | section-11, section-12 |
| section-14-outer-loop | 09, 10, 11, 12, 13 | 15 | — |
| section-15-console-ui | 14 | — | — |

## Execution Order

```
Batch 1 (parallel): section-01-config, section-02-secret-redaction, section-07-skill-extension, section-08-skill-provider
Batch 2: section-03-trace-domain (after 01)
Batch 3: section-04-trace-infrastructure (after 01, 02, 03)
Batch 4: section-09-candidate-domain (after 01, 02)
Batch 5 (parallel): section-05-otel-spans, section-06-history-store (after 04); section-10-candidate-repository (after 04, 09)
Batch 6 (parallel): section-11-proposer, section-12-evaluator, section-13-tools (after 04, 06, 07, 08, 09, 10)
Batch 7: section-14-outer-loop (after 09, 10, 11, 12, 13)
Batch 8: section-15-console-ui (after 14)
```

## Section Summaries

### section-01-config
`MetaHarnessConfig` in `Domain.Common/Config/MetaHarness/`. Nested under `AppConfig`, bound via `IOptionsMonitor`. All 15 config properties with documented defaults. `appsettings.json` section. Tests: binding and defaults.

### section-02-secret-redaction
`ISecretRedactor` interface in `Application.AI.Common/Interfaces/` and `PatternSecretRedactor` in `Infrastructure.AI/Security/`. Regex-based denylist redaction for free-text strings and config key filtering. Tests: redaction of known patterns, passthrough of clean content.

### section-03-trace-domain
Value objects in `Domain.Common/MetaHarness/`: `TraceScope` (three-tier identity: OptimizationRunId?, CandidateId?, ExecutionRunId, TaskId?), `RunMetadata`, `TurnArtifacts`, `ExecutionTraceRecord`, `HarnessScores`. `TraceScope.ForExecution()` factory. No I/O — pure domain objects and path-resolution logic. Tests: directory path resolution for all scope combinations.

### section-04-trace-infrastructure
`ITraceWriter` and `IExecutionTraceStore` interfaces in `Application.AI.Common/Interfaces/Traces/`. `FileSystemExecutionTraceStore` + scoped `FileSystemTraceWriter` in `Infrastructure.AI/Traces/`. Atomic JSON writes (temp+rename), per-file `SemaphoreSlim`, `Interlocked.Increment` for sequence numbers, `ISecretRedactor` applied before writes, dual payload storage (`payload_summary` inline, `payload_full_path` for full artifacts). Wired into `AgentExecutionContextFactory` and `ToolDiagnosticsMiddleware`. Tests: concurrent writes, atomic write correctness, redaction, payload splitting.

### section-05-otel-spans
Extend existing span processor in `Infrastructure.Observability/` to add causal OTel GenAI semantic convention attributes to tool call spans: `gen_ai.operation.name`, `gen_ai.tool.name`, `tool.input_hash` (SHA256, guarded by `IsAllDataRequested`), `tool.result_category`, `gen_ai.harness.candidate_id`, `gen_ai.harness.iteration`. No new activity sources. Tests: tag presence on tool spans, absent tags on non-tool spans, hash not computed when `IsAllDataRequested = false`.

### section-06-history-store
`IAgentHistoryStore` interface (append-only, `IAsyncEnumerable` query with `DecisionLogQuery`) in `Application.AI.Common/Interfaces/Memory/`. `JsonlAgentHistoryStore` in `Infrastructure.AI/Memory/` writing `decisions.jsonl` into the trace run directory. `ReadHistoryTool` keyed `"read_history"` in `Infrastructure.AI/Tools/`. `AgentDecisionEvent` record. Tests: append/query, `Since` checkpoint, concurrent appends, tool execution.

### section-07-skill-extension
Extend `SkillSection` type and skill parser (in `Application.AI.Common/`) to recognize `## Objectives` and `## Trace Format` headings as first-class sections. Update `ISkillMetadataRegistry`. Update `skills/research-agent/SKILL.md` with both sections. Create `skills/harness-proposer/SKILL.md` with both sections filled. Tests: parse with new sections, backward compatibility without new sections.

### section-08-skill-provider
`ISkillContentProvider` interface in `Application.AI.Common/Interfaces/Skills/`. `FileSystemSkillContentProvider` (reads from disk) and `CandidateSkillContentProvider` (reads from `HarnessCandidate.SkillFileSnapshots` in-memory). `AgentExecutionContextFactory` accepts optional `ISkillContentProvider` override. Tests: in-snapshot path returns snapshot content, out-of-snapshot path returns null.

### section-09-candidate-domain
Domain models in `Domain.Common/MetaHarness/`: `HarnessSnapshot` (skill snapshots + system prompt + config snapshot + `SnapshotManifest` with SHA256 hashes), `SnapshotEntry`, `HarnessCandidate` (immutable record, status transitions via `with`), `HarnessCandidateStatus` enum, `EvalTask`. `ISnapshotBuilder` interface in `Application.AI.Common/Interfaces/MetaHarness/`. `ActiveConfigSnapshotBuilder` in `Infrastructure.AI/MetaHarness/` — applies `ISecretRedactor`, computes SHA256 per skill file, excludes secret config keys. Tests: snapshot excludes secrets, SHA256 hashes correct, candidate `with` immutability.

### section-10-candidate-repository
`IHarnessCandidateRepository` interface in `Application.AI.Common/Interfaces/MetaHarness/`. `FileSystemHarnessCandidateRepository` in `Infrastructure.AI/MetaHarness/` — atomic writes, `candidates/index.jsonl` maintained atomically, `GetBestAsync` reads index only (O(1)), tie-breaking: pass rate → token cost → iteration. Tests: round-trip, lineage chain, best candidate tie-breaking, index-only reads.

### section-11-proposer
`IHarnessProposer` interface (`ProposeAsync → HarnessProposal`) in `Application.AI.Common/Interfaces/MetaHarness/`. `OrchestratedHarnessProposer` in `Infrastructure.AI/MetaHarness/` — delegates to `RunOrchestratedTaskCommandHandler` with proposer system prompt and trace-directory-scoped tool set (`FileSystemService`, `ReadHistoryTool`, `RestrictedSearchTool` if enabled, MCP trace resources). JSON output extraction via `JsonDocument.Parse` on first `{`…last `}` block; throws `HarnessProposalParsingException` on failure. `HarnessProposerContext` and `HarnessProposal` value objects. Tests: valid JSON output parses correctly, invalid JSON throws exception, empty changes round-trips.

### section-12-evaluator
`IEvaluationService` interface in `Application.AI.Common/Interfaces/MetaHarness/`. `AgentEvaluationService` in `Infrastructure.AI/MetaHarness/` — per-task `TraceScope` with `CandidateId`+`TaskId`, `CandidateSkillContentProvider` injection, regex grading with 5s timeout, `MaxEvalParallelism` controlled via `SemaphoreSlim`. `EvalTask` loader reading JSON files from `MetaHarnessConfig.EvalTasksPath`. Tests: pass/fail grading, regex timeout counts as fail, trace written under candidate directory, parallelism controlled.

### section-13-tools
`RestrictedSearchTool` (keyed `"restricted_search"`) in `Infrastructure.AI/Tools/` — allowlist validation (`grep`, `rg`, `cat`, `find`, `ls`, `head`, `tail`, `jq`, `wc`), shell metacharacter rejection, `Path.GetFullPath()` symlink-safe validation, 30s timeout, 1MB output cap, disabled by default (`EnableShellTool`). `TraceResourceProvider` in `Infrastructure.AI.MCP/Resources/` — MCP resources at `trace://{optRunId}/{path}`, JWT auth check, path traversal protection, disabled by flag. Tests: allowlist enforcement, metacharacter rejection, path traversal, auth enforcement.

### section-14-outer-loop
`RunHarnessOptimizationCommand` + `RunHarnessOptimizationCommandValidator` + `RunHarnessOptimizationCommandHandler` in `Application.Core/CQRS/MetaHarness/`. Full loop: seed candidate → propose → evaluate → score → track best → update `run_manifest.json` → repeat. Failure handling: parser failure and eval exception both mark `Failed` and continue. Best selection with threshold + tie-breaking. Resumability from `run_manifest.json`. Retention cleanup (`MaxRunsToKeep`). Writes `_proposed/` and `summary.md` at end. Tests: iteration count, failure continues, threshold, tie-breaking, resumability, cancellation, retention.

### section-15-console-ui
New `optimize` command in `Presentation.ConsoleUI/` invoking `RunHarnessOptimizationCommand` via `IMediator`. Interactive prompts for run description and optional iteration override. Per-iteration progress output. Completion message with path to `_proposed/`. Manual verification only (no automated test for console I/O).
