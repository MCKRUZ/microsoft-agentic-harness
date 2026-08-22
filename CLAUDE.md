# Project: Microsoft Agentic Harness

## Purpose
Production-grade template for a Microsoft Agent Framework agent with a full agentic harness — multi-skill agents, local plugins, MCP, tools, RAG, and knowledge graph systems — modeled after Claude Code's architecture. Built on the ApplicationTemplate Clean Architecture pattern. Designed for enterprise consumers to clone and extend.

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
- **Developer Onboarding Guide**: `documentation/onboarding/` — 17-page guide for engineers (deployed at `/`). Chapter 17 is the Execution API integration guide (bundles, workflows, tool discovery); its OpenAPI spec is `documentation/onboarding/assets/openapi/bundle-api.yaml` (filename kept deliberately — it is a published URL) (hand-written; to change the contract, follow *Change the wire contract* in `src/Content/Presentation/Presentation.ExecutionApi/README.md`)
- **Architecture Guide**: `documentation/architecture/` — Azure infrastructure playbook (deployed at `/architecture/`)
- **Security Guide**: `documentation/security/` — threat model, governance, sandbox, egress/SSRF, content safety, OWASP Agentic evals (deployed at `/security/`)
- **Interactive Course**: `documentation/agentic-harness-course/` — Visual course for non-technical audiences (deployed at `/agentic-harness-course/`)
- **Reference Catalogue**: `documentation/reference/` — patterns & technologies reference (deployed at `/reference/`)
- Additional design specs live in `documentation/design/` and `documentation/blueprints/` (markdown, not deployed)
- GitHub Pages workflow: `.github/workflows/pages.yml` — deploys all five sites on push to main

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
- **Implementing a subtractive or rewriting `AIContextProvider` on `ProvideAIContextAsync`.** That hook is contractually **additive**: the base merge computes `Tools = input.Concat(provided)` and `Instructions = input + provided`. A provider that filters, wraps, or replaces anything there is silently inert — every item it drops is restored from the input, and every item it keeps is published twice. To subtract or rewrite, override `InvokingCoreAsync`, call `base` first, then transform the result. A provider that only *adds* must return **just its own contribution**, never the input echoed back. This landed four separate times (`ToolPermissionFilter`, `GoverningToolContextProvider` — a security control that was dead — plus both recall providers). All four had green unit tests because those tests called the protected hook directly; **any test of an `AIContextProvider` must drive the public `InvokingAsync`**, which is what `AIContextProviderMergeContractTests` does for every provider in `Application.AI.Common`.
- **Re-implementing the conversation ownership check at a call site.** `IConversationStore` enforces it: every operation naming one conversation takes a `callerId` and throws `ConversationAccessDeniedException` for a record owned by anyone else. It did not always, and the comparison `record.UserId != callerId` ended up hand-written in **six** places across four files with three different failure shapes. Pass the authenticated caller through and let the store refuse. Two consequences: a **blank** `callerId` is an `ArgumentException`, never a wildcard; and a **mocked** store enforces nothing, so any test proving an intruder is refused must stub the throw explicitly (see `AgUiRunHandlerTests`, `ConversationOrchestratorTests`) — otherwise it passes while asserting nothing.
- **Deciding provider-failure behaviour from the .NET exception type.** Each provider SDK throws a different type for the same HTTP status — Azure OpenAI/OpenAI throw `ClientResultException`, Azure AI Inference throws `RequestFailedException`, Anthropic throws `HttpRequestException`. The old Polly `ShouldHandle` predicates listed types, so they matched **only Anthropic** — which is not in the shipped fallback chain, leaving retry and the circuit breaker inert for both providers that are. All three decisions (retry, breaker accounting, cross-provider fallback) now go through `IProviderErrorClassifier`; adding a provider means teaching the classifier its shape, never adding a type to a predicate. Two rules it encodes: a status that says *transient* (429/5xx) is **never** overridden by message text, because a false "fatal" both skips retries and halts the chain; and an unrecognised failure is `Unknown` — not retried, but still counted against provider health. Chain members are also built with the SDK's own retry **disabled** (`GetChatClientWithoutProviderRetryAsync`) so Polly is the single retry authority; the non-resilient path keeps SDK retry because nothing else wraps it.
- **Treating "no knowledge scope" as a safe default — it is not, it means GLOBAL.** `PlannerScopeFilter.VisibleTo` and `TenantIsolatedGraphStore` read a null owner as a world-readable record, so any path where identity resolution yields nothing silently publishes data. This defect has landed three separate times (host never mounted `KnowledgeScopeMiddleware`; the scope resolver rejected a token shape ownership accepted; an ambiguous claim resolved to null). The invariant: an **authenticated** request must either establish a scope or be **rejected** — never proceed unscoped. Only genuinely unauthenticated callers may run unscoped. Resolve identity solely through `ClaimsPrincipalExtensions.GetUserIdOrNull()`; never add a second precedence ladder.
- **Discriminating an outcome by a shared type's field that carries a different meaning depending on which code path produced it — the fix is a distinct type, not a better field.** Landed twice against `IacSandboxRunner`'s pre-dispatch refusal before the shape was retired for good (#421). First cut (#405/#406/#407): `ExitCode is null` looked like a clean signal for "the sandbox refused to dispatch before it ever ran," but `ProcessSandboxExecutor`/`DockerSandboxExecutor` also leave `ExitCode` null on a timeout, a reserved-env-grant rejection, and an egress-preflight block — every one of which genuinely dispatched and signed a failure attestation; silently mislabeled all three as governance denials, caught only on a second review pass. Correction: `Attestation is null`, the one field both executors leave unset in exactly the case that matters, centralized behind `WasRefusedBeforeDispatch`/`FailIfRefused`. Both fixes picked a better field; neither removed the underlying defect — a caller still had to remember to *call* the check, and the check itself could still drift to the wrong field again (the `plan`/`validate` split in that same PR's own history is exactly this: one dispatch site got the check a full commit after its sibling). #421's fix: `IacSandboxRunner.RunAsync` now returns `Result<SandboxExecutionResult>` and represents a refusal as `Result.Forbidden(...)` — a genuinely distinct outcome instead of a look-alike object with the same shape as a real one. That closes the specific defect this entry is about (a *shared field* silently meaning two different things), but it is not full compile-time enforcement: `Result<T>.Value` stays nullable regardless of `FailureType`, so every call site still does `dispatch.Value!` after checking the failure branch — a caller who skips that check still gets a null-forgiving operator the compiler accepts and a runtime `NullReferenceException`, not a compile error. The type distinction removes the "which field do I trust" ambiguity that caused this bug twice; it does not remove the need to remember to check at all. When a shared result type conflates two meanings, look first at whether the producer can return a different *type* for the different case, not just a more reliable field to check on the same one — and when adding any discriminator over a shared result type, check what every producer of that type actually sets on every path, not just the two cases in front of you.
- **Fixing one instance of a duplicated dispatch pattern and stopping there.** `TerraformGenerator.PlanAsync` runs two sequential CLI dispatches (`validate`, then `plan`); the governance-refusal guard above landed on `validate` only — `plan` needed the identical check (a hot-reloaded operator override can revoke access between the two calls, since `terraform plan` can run for minutes) and didn't get it until a second review pass noticed the new regression test only covered the first dispatch. When a fix targets one occurrence of a pattern repeated within the same method or across sibling files, grep for every other occurrence before calling the fix done — don't rely on a reviewer to find the sibling.
- **Adding a new enum member without updating every hand-maintained list that claims to mirror "all values of the enum."** `RedactionCategory.VendorApiKey` (PR #473) was added and correctly picked up by every `RedactionCategories.All`-style computed call site (`Enum.GetValues<RedactionCategory>()`), but `ContentCaptureConfig.RedactionCategories` and `LogsConfig.RedactionCategories` are separate hand-typed `List<string>` defaults — each documented in its own XML comment as "all categories from the enum" — that live in `Domain.Common` specifically so they don't have to reference `Domain.AI` (see the doc comments on both properties). Both silently stopped being true: a bare Slack/GitHub/OpenAI-shaped token would export in cleartext through the config-driven telemetry/log-redaction paths even though the new regex existed and worked everywhere else. Caught by CI's `correctness-review` gate, not by the two `/code-review` passes that preceded push. When a `RedactionCategory` (or any enum with a similar "default = every member" mirrored list) gains a member, grep for every hand-maintained string list of category names, not just the computed call sites — and add a test asserting the default list equals `Enum.GetNames<TEnum>()` so the next addition fails loudly instead of silently (see `ContentCaptureConfigValidatorTests.DefaultValues_MatchEveryRedactionCategoryEnumMember`).
- **Closing a governance check against the nearest intermediate filter instead of the value's true final, published state.** Landed twice in one PR (#476, `ToolChainBuilder`'s call-once tool registration). A plugin-sourced skill's `call-once-per-conversation` declaration was registering a tool into the process-global, never-pruned `IToolCallOncePolicy` before the plugin's `AllowedTools`/`DeniedTools` boundary had run — first fix moved registration to just *after* that boundary (`ApplyPluginBoundaryIfPluginSkill`), which closed the plugin-bypass CI's `security-review` gate found but was still one step too early: `FinalizeChain`'s `ReservedPlanCapabilityFilter` and, for any multi-skill agent, `ResolveSurvivingTools`' cross-skill first-party-precedence and drift/collision withholding both run *after* that point too — caught only by a second pass, from CI's `correctness-review` gate. A tool an untrusted MCP server declared call-once could still poison the policy for a same-named first-party tool that won the precedence fight, inverting the exact security property first-party precedence exists to provide. Correct fix: register only at a genuine whole-agent-set exit (here, `BuildToolsAsync`/`BuildMergedToolsWithSourcesAsync`), against the actual fully-filtered, fully-deduplicated surface a caller receives — never at an intermediate stage, however far downstream it looks, in a multi-stage resolution/filtering pipeline. When a fix moves a check "past" one filter, trace the value all the way to where it is actually handed to the caller before calling the fix done — a pipeline with N stages needs the check after stage N, not after whichever stage happens to be nearest the original bug.

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
