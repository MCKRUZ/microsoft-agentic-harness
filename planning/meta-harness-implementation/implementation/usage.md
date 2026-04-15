# Meta-Harness Implementation — Usage Guide

## Quick Start

### Run the optimizer interactively

```bash
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI
```

Select **Meta-Harness Optimizer** from the Agents menu, or:

```bash
dotnet run --project src/Content/Presentation/Presentation.ConsoleUI -- --example optimize
```

You will be prompted:
- **Max iterations override** — press Enter to use `MetaHarnessConfig.MaxIterations` (default: 10)

The optimizer will spin while it runs, then print the best score and path to `_proposed/`.

---

## Configuration

All settings live under `MetaHarness` in `appsettings.json` (or user secrets):

```json
{
  "MetaHarness": {
    "SeedCandidatePath": "skills/",
    "EvalTasksPath": "eval/tasks/",
    "TraceDirectoryRoot": ".meta-harness/",
    "MaxIterations": 10,
    "MaxRunsToKeep": 5,
    "ScoreImprovementThreshold": 0.01,
    "ProposerModel": "gpt-4o",
    "ProposerMaxTokens": 4096,
    "EvaluatorModel": "gpt-4o-mini",
    "EvaluatorMaxTokens": 2048,
    "EvaluatorTimeoutSeconds": 120,
    "CandidateRepositoryPath": ".meta-harness/candidates/",
    "MaxConcurrentEvals": 4,
    "EnableTracing": true,
    "RedactSecrets": true,
    "SkillContentCacheSeconds": 30
  }
}
```

**Required before first run:**
1. Place eval task JSON files in `EvalTasksPath` (e.g. `eval/tasks/task-01.json`)
2. Ensure your skill files are at `SeedCandidatePath`
3. Configure your AI deployment in the `AI` section

---

## Eval Task Format

Each file in `EvalTasksPath` is a JSON object:

```json
{
  "taskId": "task-01",
  "prompt": "Write a haiku about recursion.",
  "expectedPattern": "(?i)(recursion|itself|loop)",
  "maxPoints": 1.0
}
```

The evaluator runs each task against the candidate's skill snapshot and scores by regex match.

---

## MediatR Command API

### `RunHarnessOptimizationCommand`

```csharp
var result = await sender.Send(new RunHarnessOptimizationCommand
{
    OptimizationRunId = Guid.NewGuid(),    // required — groups all candidates for this run
    SeedCandidateId = null,               // optional — resume from a prior candidate
    MaxIterations = 5,                    // optional — overrides MetaHarnessConfig.MaxIterations
});

// result.BestScore         — pass rate [0.0, 1.0] of the best candidate
// result.IterationCount    — iterations executed
// result.ProposedChangesPath — absolute path to _proposed/ directory
// result.BestCandidateId   — Guid of winning candidate (null if no candidates evaluated)
```

---

## What Was Built (All 15 Sections)

| Section | What | Key Types |
|---------|------|-----------|
| 01 | Config | `MetaHarnessConfig`, `IOptionsMonitor<AppConfig>` |
| 02 | Secret Redaction | `ISecretRedactor`, `PatternSecretRedactor` |
| 03 | Trace Domain | `TraceId`, `SpanId`, `CausalChain` value objects |
| 04 | Trace Infrastructure | `AgentExecutionTrace`, `IExecutionTraceStore` |
| 05 | OTel Spans | Causal span attribution via `CausalSpanProcessor` |
| 06 | History Store | `IAgentHistoryStore`, `JsonlAgentHistoryStore` |
| 07 | Skill Extension | `Objectives` + `TraceFormat` sections in SKILL.md |
| 08 | Skill Provider | `ISkillContentProvider`, candidate-isolated implementation |
| 09 | Candidate Domain | `HarnessCandidate`, `HarnessSnapshot`, `ISnapshotBuilder` |
| 10 | Candidate Repository | `IHarnessCandidateRepository`, JSONL filesystem impl |
| 11 | Proposer | `IHarnessProposer`, `OrchestratedHarnessProposer`, proposal parser |
| 12 | Evaluator | `IEvaluationService`, regex grading, candidate isolation |
| 13 | Tools | `RestrictedSearchTool`, `TraceResourceProvider` |
| 14 | Outer Loop | `RunHarnessOptimizationCommand` + handler (propose→evaluate loop) |
| 15 | Console UI | `OptimizeExample`, `--example optimize` CLI command |

---

## Reviewing Results

After a run completes:

```
.meta-harness/
└── optimizations/
    └── {run-id}/
        ├── run_manifest.json       — last completed iteration, best candidate
        ├── summary.md              — per-iteration score table
        ├── _proposed/              — best candidate's skill files (ready to promote)
        └── candidates/
            └── {candidate-id}/
                └── snapshot/       — skill files at this candidate's state
```

**To promote the best candidate:**
```bash
# Copy proposed skill files over your live skills directory
cp -r .meta-harness/optimizations/{run-id}/_proposed/* skills/
```

---

## Running Tests

```bash
dotnet test src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```
