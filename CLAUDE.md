# Project: Microsoft Agentic Harness

## Purpose
Production-grade template for a Microsoft Agent Framework agent with a full agentic harness — multi-skill agents, local plugins, MCP, tools, RAG, and knowledge graph systems — modeled after Claude Code's architecture. Built on the ApplicationTemplate Clean Architecture pattern. Designed for enterprise consumers to clone and extend.

## Stack
- C# .NET 10, Clean Architecture, CQRS/MediatR, FluentValidation, AutoMapper
- Microsoft.Agents.AI, Microsoft.Extensions.AI, OpenAI SDK (Azure OpenAI runs on its `/openai/v1/` route)
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
- **MediatR Pipeline**: validation, authorization, idempotency, caching, tracing and timeout from `Application.Common`, then the AI behaviors (unhandled-exception safety net, content safety, token budget, tool output compression and others) from `Application.AI.Common`, in registration order
- **DAG Plan Execution**: PlanExecutor orchestrates PlanGraph with bounded concurrency, checkpoint/resume via EfCorePlanStateStore, error recovery (retry/escalate/skip)
- **Sandbox Isolation**: ProcessSandboxExecutor (Job Objects) and DockerSandboxExecutor with HMAC attestation. Closed-by-default capability model
- **Step Executors**: Keyed DI by StepType enum — LlmCall, ToolUse, HumanGate, ConditionalBranch, SubPlanInvocation
- **Partial Class Pattern**: Large files split into partials by responsibility (PlanExecutor.Scheduling.cs, PlanExecutor.Recovery.cs, etc.)
- **Skill Training Loop**: `TrainSkillCommandHandler` chains the 6 stages on the same call stack (no MediatR re-entrance inner-loop); epoch-boundary mechanisms (SlowUpdate, MetaSkillUpdate) are separate CQRS commands dispatched via `IMediator` so they get the standard pipeline (validation, audit, telemetry)

## Documentation
- **Developer Onboarding Guide**: `documentation/onboarding/` — 19-chapter guide for engineers (deployed at `/`). Chapter 19, the Incident Ledger, holds every `INC-NN` below. Chapter 17 is the Execution API integration guide (bundles, workflows, tool discovery); its OpenAPI spec is `documentation/onboarding/assets/openapi/bundle-api.yaml` (filename kept deliberately — it is a published URL) (hand-written; to change the contract, follow *Change the wire contract* in `src/Content/Presentation/Presentation.ExecutionApi/README.md`)
- **Architecture Guide**: `documentation/architecture/` — Azure infrastructure playbook (deployed at `/architecture/`)
- **Security Guide**: `documentation/security/` — threat model, governance, sandbox, egress/SSRF, content safety, OWASP Agentic evals (deployed at `/security/`)
- **Interactive Course**: `documentation/agentic-harness-course/` — Visual course for non-technical audiences (deployed at `/agentic-harness-course/`)
- **Reference Catalogue**: `documentation/reference/` — patterns & technologies reference (deployed at `/reference/`)
- Additional design specs live in `documentation/design/` and `documentation/blueprints/` (markdown, not deployed)
- **Capability Showcase**: `documentation/showcase/` — interactive architecture showcase (deployed at `/showcase/`)
- GitHub Pages workflow: `.github/workflows/pages.yml` — deploys all six sites on push to main

## Commands
- `dotnet build src/AgenticHarness.slnx` — Build
- `dotnet test src/AgenticHarness.slnx` — Run all tests
- `dotnet test --collect:"XPlat Code Coverage"` — Tests with coverage
- `dotnet run --project src/Content/Presentation/Presentation.ConsoleUI` — Run console

## Verification
After changes: build, then test (commands above).

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

- Match or exceed the reference implementation on patterns, abstractions, and rigor. Never ship something here that you'd reject when reviewing the reference.
- When the reference is silent on a problem, design the best answer; do not invent a smaller answer just because the reference didn't address it.
- Every public type ships with full XML docs (already a rule) — same reasoning. Consumers are reading this as teaching material.

## Standing rules
Each arrow names the enforcing test, the review rubric, or the ledger entry. Subsystem rules: `.claude/rules/*.md`. `check-claude-md-budget.mjs` caps this file at 12,288 bytes and checks every arrow.
- A request carrying user text must implement `IContentScreenable`; `ContentSafetyBehavior` screens nothing else → INC-05
- `AIContextProvider.ProvideAIContextAsync` is additive: subtract or rewrite in `InvokingCoreAsync`; test via `InvokingAsync` → AIContextProviderMergeContractTests
- Never re-check conversation ownership at a call site; pass `callerId` and let `IConversationStore` refuse (a mock refuses nothing) → INC-18
- Retry, breaker and fallback decide via `IProviderErrorClassifier`, never exception type; 429/5xx stay transient → DefaultProviderErrorClassifierTests
- An authenticated request gets a knowledge scope or is rejected (null = GLOBAL); identity only via `GetUserIdOrNull()` → CallerIdentityResolutionBoundaryTests
- When one result type means two things, return a distinct type, not a better field → correctness-review-rubric
- A fix to one occurrence of a repeated pattern is not done until every sibling has it → correctness-review-rubric
- A new enum member updates every hand-kept "all values" list; pin each with a test → DefaultValues_MatchEveryRedactionCategoryEnumMember
- Close a check at the pipeline's true final exit, not after the nearest filter → correctness-review-rubric
- Nothing built inside the `ILoggerFactory` (or any core) factory takes `ILogger<T>`; sweep after core DI changes → ValidateOnBuildSweepTests
- Before changing a shared object, read what each real consumer does with it → correctness-review-rubric
- A control counts only if a host invokes it: name the line whose deletion removes it and the test that then fails → SecurityControlHasACallerTests
- Narrowing a check: also test the other direction (no new false negative) against a real producer's wire shape → correctness-review-rubric
- Per-caller MCP credentials need caller identity in `McpConnectionManager`'s cache keys → SharedCaches_AreKeyedByServerNameOnly
- Arm child governance only via `GovernanceArmer` (a preset; `OpenChild` for child scopes), never a hand copy → GovernanceArmerTests
- Publish ambient state around every step of an async iterator, not once; re-assert attribution via `ReassertAttribution()` → AttributionIsPublishedByTheExecutionContextOnlyTests
- Package skew: pin the transitive package in `Presentation.Common.csproj` or list it in `KnownHostSkews` with a reason → PackageSkewTests

<!-- gitnexus:start -->

# GitNexus — Code Intelligence

This project is indexed by GitNexus as **microsoft-agentic-harness**. Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

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
