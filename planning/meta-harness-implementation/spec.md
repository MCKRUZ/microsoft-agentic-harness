# Meta-Harness Implementation Spec

Implement all Meta-Harness concepts from the paper "Meta-Harness: End-to-End Optimization of Model Harnesses" (arXiv:2603.28052) into this Microsoft Agentic Harness project.

## Background: What the Paper Proposes

Meta-Harness is an outer-loop optimization system that automatically searches over harness code to improve LLM performance. The key insight: instead of compressing feedback into scores/summaries, give a coding-agent proposer access to a **filesystem** containing full execution traces (prompts sent, tool calls, model outputs, state updates) from every prior run. The proposer uses grep/cat to navigate this filesystem non-Markovianly (reads ~82 files per iteration, 41% source code + 40% execution traces), reasons causally about failures, and proposes improved harness code.

Key results from the paper:
- 7.7 point improvement over state-of-the-art context management using 4x fewer tokens
- 4.7 point improvement on IMO-level math problems across 5 held-out models
- Surpasses best hand-engineered baselines on TerminalBench-2 (agentic coding benchmark)

## What to Implement

### 1. Execution Trace Persistence Infrastructure
- `IExecutionTraceStore` interface in `Application.AI.Common/Interfaces/`
- Per-turn artifact writer: stores system prompt snapshot, tool call log, model response, state updates to a structured directory `traces/{runId}/turn_{n}/`
- `FileSystemExecutionTraceStore` in `Infrastructure.AI/`
- Wire through DI and agent execution pipeline

### 2. Execution Trace Structure (grep-friendly)
- Each turn directory contains: `system_prompt.md`, `tool_calls.jsonl`, `model_response.md`, `state_snapshot.json`, `scores.json`
- Run-level: `manifest.json` (run metadata), `summary.md` (human-readable outcome)
- Design for queryability — the agent proposer needs to grep/cat these files

### 3. Causal Span Attribution in OpenTelemetry
- Extend existing OTel tracing to tag tool call spans with: `tool.name`, `tool.input_hash`, `tool.result_category` (success/failure/timeout/blocked), `tool.decision_rationale`
- Each span should capture the decision that triggered it, inputs considered, and outcome
- Structured for retrospective causal reading, not just metrics aggregation

### 4. Queryable Agent History (Non-Markovian Memory)
- `IAgentHistoryStore` interface — append-only log of prior decisions, not just current context
- `decisions.jsonl` per session that tools can read back
- Expose a `read_history` tool so the agent can query its own prior decisions during execution

### 5. Skill Text as Optimization Interface (SKILL.md Extension)
- Add `## Objectives` section to SKILL.md format — defines what the agent is optimizing for, success criteria, failure patterns to avoid
- Add `## Trace Format` section documenting the execution trace directory structure
- Update SKILL.md parser to extract and surface these sections to the agent context
- Update `skills/research-agent/SKILL.md` as reference implementation

### 6. Meta-Harness Outer Loop (the optimization loop itself)
- `IHarnessProposer` interface — takes filesystem access + current harness + trace history, proposes improved harness
- `MetaHarnessOrchestrator` — outer loop: evaluate current harness → store traces → invoke proposer → apply proposed changes → repeat
- `RunHarnessOptimizationCommand` + handler (CQRS)
- Proposer is itself a coding agent with access to the trace filesystem
- Score tracking per harness candidate

### 7. Harness Candidate Management
- `HarnessCandidate` domain model — source snapshot, scores, run metadata, parent candidate ID
- `IHarnessCandidateRepository` — stores candidates with lineage (parent/child relationships for causal attribution)
- Each candidate gets its own directory in the trace filesystem

### 8. Config & AppSettings
- `MetaHarnessConfig` options class — search budget (max iterations), population size, score threshold, trace directory root
- Wire into `AppConfig` hierarchy and `appsettings.json`

## Architecture Constraints
- Clean Architecture: Domain → Application → Infrastructure → Presentation
- Follow existing patterns: keyed DI, MediatR CQRS, Result<T>, FluentValidation on commands
- Interfaces in `Application.AI.Common/Interfaces/`, implementations in `Infrastructure.AI/`
- 80% test coverage on new code
- Functions <50 lines, files <400 lines
- No hardcoded config — everything through `IOptionsMonitor<MetaHarnessConfig>`
- Reference `C:\CodeRepos\ApplicationTemplate` for existing patterns before inventing new ones
