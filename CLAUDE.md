# Project: Microsoft Agentic Harness

## Purpose
Production-grade template for a Microsoft Agent Framework agent with a full agentic harness — multi-skill agents, local plugins, MCP, tools, RAG, and knowledge graph systems — modeled after Claude Code's architecture. Built on the ApplicationTemplate Clean Architecture pattern. Designed for enterprise consumers to clone and extend.

## RAG & Knowledge Architecture
The harness includes a full RAG pipeline (`Infrastructure.AI.RAG`) and a production knowledge graph layer (`Infrastructure.AI.KnowledgeGraph`) inspired by [Cognee](https://github.com/topoteretes/cognee).

### RAG Capabilities
- **Ingestion**: 3 chunking strategies (structure-aware, fixed-size, semantic), contextual enrichment (Anthropic pattern), RAPTOR hierarchical summarization
- **Retrieval**: Hybrid dense+sparse via Reciprocal Rank Fusion, query transformation (RAG Fusion, HyDE), query classification/routing
- **Quality**: CRAG evaluation with refinement loops, configurable accept/refine/reject thresholds
- **Assembly**: Token budget enforcement, pointer expansion (sibling/parent), citation tracking
- **Reranking**: Azure Semantic, Cross-Encoder, NoOp (strategy-keyed DI)
- **Stores**: Azure AI Search + FAISS (vector), Azure AI Search + SQLite FTS5 (BM25)
- **Complexity Routing** (Phase A): LLM-based query complexity classification, tiered pipeline selection, 30-50% cost reduction on mixed workloads
- **Multi-Hop** (Phase B): Query decomposition, iterative retrieval with sufficiency evaluation, answer faithfulness evaluation for hallucination detection
- **Full Autonomy** (Phase D): Multi-source parallel orchestration (vector + BM25 + graph), retrieval cost tracking, quality gates at each pipeline stage

### Knowledge Graph (Implemented — from Cognee analysis)
1. **Production Graph Backend** — Neo4j, Kuzu, and PostgreSQL backends behind `IGraphDatabaseBackend`. Entity extraction, Leiden community detection (`LeidenCommunityDetector`), in-memory store for development
2. **Feedback-Weighted Search** — `GraphFeedbackStore` + `LlmFeedbackDetector` track retrieval quality on graph nodes/edges. Future retrievals blend semantic relevance with historical feedback weights
3. **Cross-Session Knowledge Persistence** — `Remember()`/`Recall()`/`Forget()`/`Improve()` via `IKnowledgeMemory`. `InMemorySessionCache` for fast reads with background sync to `ICrossSessionMemoryStore`. `MemoryDecayService` handles configurable decay tiers (CRITICAL/STANDARD/EPHEMERAL)
4. **Entity-Level Provenance** — `DefaultProvenanceStamper` stamps every node/edge with source pipeline, task, and timestamp. `ComplianceAwareGraphStore` enforces retention policies. `DefaultErasureOrchestrator` handles right-to-erasure with `ErasureReceipt` records
5. **Multi-Tenant Knowledge Isolation** — `TenantIsolatedGraphStore` enforces per-record boundaries by **tenant AND owner** via `IKnowledgeScopeValidator`: a record is visible only when its tenant matches (or is global/null) and its owner matches (or is shared-in-tenant/null). Identity is captured at the entry point (`KnowledgeScopeMiddleware`/`KnowledgeScopeHubFilter`) and flows ambiently (`AsyncLocal`) into child scopes + post-turn background writes; `ComplianceAwareGraphStore` stamps `TenantId` on write (owner stays writer-authoritative). Memory is scope-namespaced (`memory:{tenant}:{user}:{key}`). Enforced across **all three backends** — in-memory, Neo4j, and PostgreSQL all persist and filter `OwnerId`/`TenantId` (Postgres self-initializes its schema)

### Governance Subsystems
- **Drift Detection**: EWMA-based quality monitoring against baselines, three severity levels, DriftEscalationBridge
- **Learnings**: CQRS-based knowledge capture with exponential decay, scheduled pruning, drift integration
- **Escalation**: Multi-approval workflows (AllOf/AnyOf/Quorum), JSONL audit, AG-UI notifications
- **Autonomy Tiers**: Manual/Supervised/Autonomous enforcement via MediatR pipeline behavior, response sanitizers
- **Resilience**: Polly circuit breakers, provider fallback chains, health state tracking

### Skill Training (SkillOpt port)
Single-skill-document optimizer modeled on [microsoft/SkillOpt](https://github.com/microsoft/SkillOpt). 6-stage loop (rollout → reflect → aggregate → select → apply → gate) in `Application.AI.Common/CQRS/SkillTraining/TrainSkill/`. Bounded `Edit{Op,Target,Content}` operations (Append/InsertAfter/Replace/Delete) against the SKILL.md, validated by a gate that uses Hard/Soft/Mixed metric projection and strict-greater accept semantics. Epoch boundary runs SlowUpdate (paired longitudinal forgetting detection) and MetaSkillUpdate (cross-epoch strategy memory via `IKnowledgeMemory`). Pure components (`PatchApplier`, `GateEvaluator`, `PatchAggregator`, `TopKEditSelector`, 3 LR schedulers) are stateless. `IPatchProposer` + `IRolloutRunner` ship with `NotConfigured*` fail-fast defaults — consumers replace with agent-backed Infrastructure impls.

## Stack
- C# .NET 10, Clean Architecture, CQRS/MediatR, FluentValidation, AutoMapper
- Microsoft.Agents.AI, Microsoft.Extensions.AI, Azure.AI.OpenAI
- MCP (Model Context Protocol) server/client — HTTP transport with JWT auth
- EF Core with SQLite (plan state persistence), IDbContextFactory for short-lived contexts
- Docker.DotNet (container sandbox)
- RAG: Azure AI Search, FAISS, SQLite FTS5, ManagedCode.GraphRag
- Knowledge Graph: Neo4j, Kuzu, PostgreSQL, Leiden community detection
- Governance: Polly (resilience), EWMA drift detection, JSONL audit stores
- OpenTelemetry (Grafana + Tempo + Prometheus + Azure Monitor)
- xUnit, Moq, coverlet

## Architecture
Clean Architecture with Domain → Application → Infrastructure → Presentation layers.
Reference implementation: `C:\CodeRepos\ApplicationTemplate` (same layer structure, DI patterns, and conventions).

Key architectural concepts from the reference:
- **Skills System**: Multi-skill agents with prerequisite ordering, dual mode (Managed/Injected), plugin-sourced skills
- **Plugin System**: Local plugin declarations with manifest reading, skill + MCP server wiring, boundary governance (AllowedTools/DeniedTools/AutonomyLevel)
- **Tool Output Compression**: MediatR pipeline behavior with content-type detection and strategy-specific compression
- **Keyed DI Tools**: Tools registered with string keys (`"file_system"`, `"calculation_engine"`) for lazy resolution from skill declarations
- **Agent Manifest (AGENT.md)**: Declarative agent config with tool declarations, state config, decision frameworks
- **MCP Server**: ASP.NET Core WebAPI exposing tools/prompts/resources via MCP protocol
- **Factory Pattern**: AgentFactory, ChatClientFactory, AgentExecutionContextFactory for consistent agent construction
- **MediatR Pipeline**: Validation → Caching → Performance → Tool Output Compression → Exception handling behaviors
- **DAG Plan Execution**: PlanExecutor orchestrates PlanGraph with bounded concurrency, checkpoint/resume via EfCorePlanStateStore, error recovery (retry/escalate/skip)
- **Sandbox Isolation**: ProcessSandboxExecutor (Job Objects) and DockerSandboxExecutor with HMAC attestation. Closed-by-default capability model
- **Step Executors**: Keyed DI by StepType enum — LlmCall, ToolUse, HumanGate, ConditionalBranch, SubPlanInvocation
- **Partial Class Pattern**: Large files split into partials by responsibility (PlanExecutor.Scheduling.cs, PlanExecutor.Recovery.cs, etc.)
- **Skill Training Loop**: `TrainSkillCommandHandler` chains the 6 stages on the same call stack (no MediatR re-entrance inner-loop); epoch-boundary mechanisms (SlowUpdate, MetaSkillUpdate) are separate CQRS commands dispatched via `IMediator` so they get the standard pipeline (validation, audit, telemetry)

## Documentation
- **Developer Onboarding Guide**: `documentation/onboarding/` — 14-page guide for engineers (deployed at `/`)
- **Architecture Guide**: `documentation/architecture/` — Azure infrastructure playbook (deployed at `/architecture/`)
- **Interactive Course**: `documentation/agentic-harness-course/` — Visual course for non-technical audiences (deployed at `/agentic-harness-course/`)
- GitHub Pages workflow: `.github/workflows/pages.yml` — deploys all three on push to main

## Commands
- `dotnet build src/AgenticHarness.slnx` — Build
- `dotnet test src/AgenticHarness.slnx` — Run all tests
- `dotnet test --collect:"XPlat Code Coverage"` — Tests with coverage
- `dotnet run --project src/Content/Presentation/Presentation.ConsoleUI` — Run console

## Verification
After changes: `dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`

## Code Style
- Immutability: records, `with` expressions, `ImmutableList<T>`, init-only properties
- PascalCase (classes/methods/props), `_camelCase` (private fields), camelCase (locals/params)
- Functions <50 lines, no nesting >4 levels
- Result<T> pattern for error handling, structured logging (no console.log)
- FluentValidation on all DTOs, validate at system boundaries

## Task Approach
1. Check reference implementation at `C:\CodeRepos\ApplicationTemplate` for existing patterns before creating new abstractions
2. Present options when trade-offs exist between agent framework approaches
3. Implement in layers: Domain models first, Application interfaces, Infrastructure implementations, Presentation last
4. Run build + tests after each meaningful change
5. Flag anything that diverges from the ApplicationTemplate patterns

## Quality Bar

This is a production template that enterprise consumers will clone. Corners cut now propagate to every downstream consumer. The global "Optimize for Best Outcome, Not Speed" rule applies with extra weight here:

- Match or exceed the reference implementation (`C:\CodeRepos\ApplicationTemplate`) on patterns, abstractions, and rigor. Never ship something here that you'd reject when reviewing the reference.
- When the reference is silent on a problem, design the best answer; do not invent a smaller answer just because the reference didn't address it.
- Every public type ships with full XML docs (already a rule) — same reasoning. Consumers are reading this as teaching material.

## Common Mistakes
- Creating new abstractions when ApplicationTemplate already has one: check `Application.AI.Common/Interfaces/` first
- Registering tools without keyed DI: always use `AddKeyedSingleton<T>(toolName, ...)` pattern
- Forgetting MediatR pipeline behaviors when adding new commands: register in `DependencyInjection.cs`
- Hardcoding AI model config: use `AppConfig.AI.AgentFramework` section, never inline
- Skipping content safety middleware in agent factory: always wire through `AgentFactory`
- Creating step executors without registering them as keyed services by StepType
- Using SandboxOptions (Domain config) when you need SandboxExecutionOptions (Application container config) — they're different classes
- Manually incrementing entity Version — SqliteVersionInterceptor handles this on save
- Forgetting to check Result.IsSuccess on state store operations
- Adding NotifyStepStarted in step executors — PlanExecutor owns notification centrally
- Using `PluginSource` on a skill without declaring the plugin in `PluginsConfig`: the plugin won't load and tools won't resolve
- Forgetting that DeniedTools on a plugin are bypass-immune: they can't be overridden by auto-approve modes
- Using `Replace` with empty `Content` to remove text in a `Patch` — `PatchApplier` rejects it as a failed edit; use `Delete` op explicitly so the audit trail captures intent
- Returning raw exception text in `Result.Fail` from skill-training handlers — must use stable scrubbed codes (`skill_training.*`) and log the full exception via structured logging; HTTP-backed proposers can leak SAS tokens in exception messages otherwise
- Persisting projected gate scores across checkpoint reloads — round-trip float→text→float can flip Accept/Reject by 1 ULP; orchestrator should re-project on each call via `IGateEvaluator.SelectGateScore`
- Forgetting the `NotConfiguredPatchProposer` / `NotConfiguredRolloutRunner` defaults — these throw on first use; a consumer that invokes `TrainSkillCommand` without registering real impls gets an `InvalidOperationException` at runtime, not a silent no-op

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **microsoft-agentic-harness** (37816 symbols, 86770 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/microsoft-agentic-harness/context` | Codebase overview, check index freshness |
| `gitnexus://repo/microsoft-agentic-harness/clusters` | All functional areas |
| `gitnexus://repo/microsoft-agentic-harness/processes` | All execution flows |
| `gitnexus://repo/microsoft-agentic-harness/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
