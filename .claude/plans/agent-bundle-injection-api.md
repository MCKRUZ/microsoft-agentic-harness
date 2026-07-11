# Plan: Inject & Run Agent Bundles via API (Issue #154)

## Problem
A first-party external system authors self-contained agent bundles (AGENT.md + SKILL.md files +
plugin manifests) and wants to hand one to *this* solution over HTTP and have it run. This solution
is the **execution host** for agents it did not write. We are **not** the system of record — bundles
are short-lived; run results are returned, not persisted long-term.

## Decisions locked with Matt
| # | Decision |
|---|----------|
| Trust | **First-party** authors → moderate trust; confine as defense-in-depth |
| Bundle | **Self-contained** archive (AGENT.md + SKILL.md files + plugin manifests) |
| Lifecycle | **Short-lived TTL handle** (register → handle → invoke); not system of record |
| Delivery | **Async-job is the contract; streaming is opt-in** |
| Capability model | **Host grants from a fixed per-caller envelope; bundle *requests*; beyond-envelope is rejected, not honored** |

## The load-bearing security posture
1. **Capability envelope per caller.** The bundle's self-declared `AllowedTools` / autonomy / MCP refs
   are *requests*. The host's per-credential envelope is the *grant*. Enforced by the EXISTING gate
   chain (`GovernedAIFunction` → `IToolInvocationGovernor` → `ThreePhasePermissionResolver`) by adding
   one new `IPermissionRuleProvider` that emits **bypass-immune Deny** for out-of-envelope tools + a
   tier-ceiling baseline. Zero new gate code.
2. **Governance forced ON for bundle runs.** `GovernanceConfig.EnforceToolInvocation` is opt-in and
   OFF by default (governor is pass-through when off). The bundle-run path **forces it on** regardless
   of global config — otherwise the envelope is inert.
3. **MCP endpoints are reference-only.** Bundles may name host-registered MCP servers, never define
   endpoints. This closes the SSRF-by-construction hole. (This is the ONE genuinely new enforcement
   point — no per-caller MCP allowlist exists today; added as a filter in `ToolChainBuilder`.)
4. **Archive is hostile input.** Zip-slip (path-traversal) guard on every entry; decompression-bomb
   guard (max total size, max entry count, max ratio). Reject at ingest before anything touches disk.
5. **Guaranteed temp-dir cleanup** on handle expiry AND on crash/timeout.

## Core-Model Pass — PREREQUISITE (Option A, decided with Matt)

The agent is a **hollow shell** today: `AgentDefinition` is pure metadata + a list of skill IDs, with
no instructions and no tool ceiling. Everything substantive lives on `SkillDefinition`. This is the
un-Claude part. Option A promotes the agent to **first-class** (own system prompt + own tool ceiling),
while skills stay composable and can be **agent-owned (nested)** OR **shared (global pool)** — reuse
preserved. A self-contained bundle is then just "a first-class agent that owns its skills," so this
pass is the foundation the bundle API sits on, not a detour.

**Threading route (low-churn):** extend `SkillAgentOptions` (`Domain.AI/Skills/SkillAgentOptions.cs`)
with `AgentInstructions` + `AllowedTools`; populate at `ExecuteAgentTurnCommandHandler.cs:98-103`;
consume in `AgentExecutionContextFactory.MapToAgentContextAsync`. No signature churn through the
cache/factory chain. `AgentExecutionContextFactory` does NOT currently receive the `AgentDefinition`
(it's reduced to skill IDs at `ExecuteAgentTurnCommandHandler.cs:91-93` and thrown away) — this route
restores the agent's substance without touching the cache key.

### Pinned seams (verified, with line numbers)
- **S1 Agent instructions.** AGENT.md body is discarded at `AgentMetadataParser.cs:36` (`var (yaml, _)`).
  Capture it → new `Instructions` init-prop on `AgentDefinition` (record ends `AgentDefinition.cs:56`).
  Prepend before skill merge at `AgentExecutionContextFactory.cs:105`
  (`SkillInstructionMerger.Merge(...)`); flows onto the agent via `AgentExecutionContext.Instruction`
  (line 204) → `AgentFactory.cs:131` `ChatClientAgentOptions.Instructions`. Prepending at 105 also
  covers the composer path (line 107 wraps whatever `instruction` holds).
- **S2 Tool ceiling.** The ceiling wire EXISTS but is unused from the agent path (`allowedTools` param
  is `null` at `AgentExecutionContextFactory.cs:450`). Apply `agentCeiling ∩ (union of skills'
  AllowedTools)` in BOTH filters: pass the intersection as the `allowedTools` arg at
  `AgentExecutionContextFactory.cs:109` (→ `ToolChainBuilder.BuildMergedToolsWithSourcesAsync`
  filter at lines 172-176) AND construct the `ToolPermissionFilter` at line 382 from the same
  intersection. **Intersect, never union** — can only tighten. Allowlist is a plain string list.
- **S3 Agent-owned nested skills.** `AgentMetadataRegistry.DiscoverInDirectory` (after parsing the
  AgentDefinition ~line 154): scan `<agentDir>/skills/*/SKILL.md` via
  `SkillMetadataParser.ParseFromFile(skillFile, dir, agentSourceMarker)`, register into a **per-agent
  store** (keyed by agent Id) so they never enter the global `SkillMetadataRegistry._cache`. Resolution
  in `AgentFactory.CreateAgentWithContextFromSkillsAsync` (line 442) checks the agent-owned store
  before `_skillRegistry.TryGet(id)`. Reuses the existing `SkillDefinition.PluginSource` ownership-tag
  pattern (`ResolveOwningPlugin`, `SkillMetadataRegistry.cs:294-315`).

### Core-model phasing (before the bundle API)
- **CM-PR1 — Agent instructions.** S1. AGENT.md body → `AgentDefinition.Instructions` → prepended
  system prompt. Tests: agent block leads the merged skill instructions.
- **CM-PR2 — Agent tool ceiling.** S2. Parse agent `AllowedTools`; intersect (tighten-only). Tests:
  ceiling strips out-of-envelope skill tools, never widens. *(security-reviewer — permission surface)*
- **CM-PR3 — Agent-owned nested skills.** S3. `<agentDir>/skills/` discovery + per-agent store +
  resolution precedence. Tests: nested skill resolvable by its agent, invisible to other agents, global
  pool unpolluted.
- **CM-PR4 — Delete the test-only `AgentManifest` class.** CORRECTED: `StateConfiguration` /
  `DecisionFramework` are NOT dead — they're live in the StateManagement/checkpoint subsystem and are
  fields on `SkillDefinition`. Do NOT delete them. The only inert thing is the `AgentManifest` class
  itself (instantiated only in tests; prod references are doc `<see cref>` links). Once `AgentDefinition`
  is first-class it fully supersedes `AgentManifest` → delete the class + its test file. Tiny, safe.

## Key architecture facts (from subsystem research)
- **Rich `AgentManifest` is inert.** Production execution is driven by `SkillDefinition` (SKILL.md),
  resolved through `IMediator.Send(ExecuteAgentTurnCommand | RunConversationCommand)`. Do NOT touch
  `AgentFactory`/`AgentConversationCache` directly — send the MediatR command.
- **Tier 2/3 skill disclosure reads from DISK** via the framework's `UseFileSkill(path)`. So a pure
  in-memory parse is impossible. Answer: **extract the bundle to a temp dir**, point the machinery at
  it, run, delete on TTL. Reuses everything.
- **Registries are immutable-after-startup singletons** scanning global config paths — no runtime
  add/remove/overlay. Per-run ephemeral agents need a **per-run `AsyncLocal` overlay** consulted by a
  composite registry that falls back to the global singleton. Matches existing `AsyncLocal` precedent
  (`ToolGovernanceAccessor`, `KnowledgeScopeAccessor`, `AgentTurnStreamSink`).
- **No generic async-job registry exists.** Reuse the `ChangeProposalBackgroundService` + `Channel`
  pattern (BackgroundService, scope-per-item). Build a small in-memory, TTL'd job store.
- **Streaming seam exists.** `IAgentTurnStreamSink` (AsyncLocal) + AG-UI SSE (`AgUiEventWriter`).
- **No `RegisterPostEvictionCallback` usage anywhere** — temp-dir-on-expiry cleanup is net-new; pair
  IMemoryCache sliding expiration with a post-eviction callback + a belt-and-suspenders idle sweeper
  (`SessionIdleCleanupService` precedent).

## API contract
```
POST   /api/bundles                         multipart zip → 201 { handle, expiresAt }   (validate+extract+overlay)
POST   /api/bundles/{handle}/runs           { userMessages[], maxTurns?, stream? } → 202 { jobId, statusUrl }
GET    /api/bundles/{handle}/runs/{jobId}   → { status, result?, error?, tokens? }    (poll)
GET    /api/bundles/{handle}/runs/{jobId}/stream   text/event-stream (AG-UI events)   (opt-in)
DELETE /api/bundles/{handle}                → 204   (explicit cleanup; TTL also handles it)
```
- Run contract exposes **multi-turn** (`RunConversationCommand` superset: `userMessages`, `maxTurns`).
- Job records are **in-memory, TTL'd** (aligns with "not system of record").
- Envelope resolved per-credential (subject claim) with role/default fallback.

## Layer-by-layer file breakdown (Clean Architecture)

### Domain (`Domain.AI`)
- `Bundles/CapabilityEnvelope.cs` — value object: `AllowedTools`, `AutonomyCeiling`, `AllowedMcpServers`, limits.
- `Bundles/BundleRunStatus.cs` — enum: Queued/Running/Succeeded/Failed.
- `Bundles/BundleRunRecord.cs` — job record value object (id, handle, status, result, error, tokens, timestamps).

### Application (`Application.AI.Common` / `Application.Core`)
- `Interfaces/Bundles/IBundleStagingService.cs` — extract+validate archive → staged bundle (temp path + overlay defs).
- `Interfaces/Bundles/IEphemeralAgentOverlay.cs` + accessor — AsyncLocal overlay of agent/skill defs for one run.
- `Interfaces/Bundles/IBundleRunJobStore.cs` — create/get/update job record; TTL.
- `Interfaces/Bundles/IBundleRunDispatchQueue.cs` — Channel enqueue/drain.
- `Interfaces/Governance/ICapabilityEnvelopeResolver.cs` — ClaimsPrincipal → CapabilityEnvelope.
- `Services/Governance/CapabilityEnvelopeAccessor.cs` — AsyncLocal<CapabilityEnvelope?>.
- `Services/Governance/EnvelopePermissionRuleProvider.cs` — reads ambient envelope → bypass-immune Deny + tier ceiling.
- `CQRS/Bundles/RegisterBundle/…` (Command + Handler + Validator) — stage bundle, return handle.
- `CQRS/Bundles/RunBundle/…` (Command + Handler) — set overlay+envelope ambient, dispatch background run.
- `CQRS/Bundles/GetBundleRun/…` (Query + Handler) — read job record.
- Validators: `RegisterBundleCommandValidator` (archive limits), `BundleManifestValidator`.

### Infrastructure (`Infrastructure.AI`)
- `Bundles/BundleStagingService.cs` — zip-slip + bomb guards → temp dir; parse AGENT.md/SKILL.md/plugin.json → overlay defs whose FilePath points into temp dir.
- `Bundles/EphemeralAgentOverlay.cs` — AsyncLocal overlay impl.
- `Agents/OverlayAwareAgentMetadataRegistry.cs` + `Skills/OverlayAwareSkillMetadataRegistry.cs` — composite decorators over the global singletons.
- `Bundles/InMemoryBundleRunJobStore.cs` — IMemoryCache-backed, sliding TTL, post-eviction temp-dir cleanup.
- `Bundles/BundleRunBackgroundService.cs` + `Bundles/InMemoryBundleRunDispatchQueue.cs` — mirror ChangeProposal pattern.
- `Bundles/BundleTempWorkspaceCleanupService.cs` — idle sweeper backstop.
- `Governance/CapabilityEnvelopeResolver.cs` — config-driven per-caller envelope.
- Parser content overloads: `AgentMetadataParser.ParseFromContent`, `SkillMetadataParser` pure-string overload, `PluginManifestReader.ReadFromJson`.
- MCP envelope filter in `ToolChainBuilder` (Injected path + MCP provisioning) — filter by envelope's allowed server names. **(the one new gate)**

### Presentation (`Presentation.AgentHub` OR new `Presentation.BundleApi` — see open decision)
- `Controllers/BundlesController.cs` — the REST endpoints; `[Authorize]`, role-gated, per-path rate limits.
- Middleware: extend scope capture to also set ambient `CapabilityEnvelope` from ClaimsPrincipal.
- SSE stream endpoint reusing `AgUiEventWriter` + `AgentTurnStreamSink`.
- DI: register composite registries (decorate singletons), job store, background services, staging, resolver, `EnvelopePermissionRuleProvider`; force `EnforceToolInvocation` on the bundle-run scope.

### Config (`Domain.Common/Config`)
- `AI/BundleExecution/BundleExecutionConfig.cs` — `Enabled`, per-caller envelopes, TTLs, archive limits (max size / entries / ratio), temp root. Default OFF.

## Phasing (one PR per phase, security-gated where marked)
- **PR1 — Ingestion + overlay (no API).** Content-parse overloads, `BundleStagingService` (zip-slip +
  bomb guards), `EphemeralAgentOverlay` + composite registries. Proven by unit tests. *(security-reviewer)*
- **PR2 — Capability envelope enforcement.** `CapabilityEnvelope`, resolver, accessor,
  `EnvelopePermissionRuleProvider`, MCP allowlist filter, forced `EnforceToolInvocation`. Tests prove
  out-of-envelope tool / MCP / autonomy is denied. *(security-reviewer)*
  - **Scoped 2026-07-10, two sub-decisions LOCKED with Matt:**
    1. *Force-enforce seam* = **lightweight ambient flag** (`AsyncLocal<bool>` override accessor,
       OR-ed into `ToolInvocationGovernor`'s two enforce reads — line 88 `AuthorizeAsync`, line 270
       `GetTrace`). `GovernanceConfig.EnforceToolInvocation` is `init`-only → cannot be rebound
       per-scope, so an ambient override is the only viable shape. Matches existing accessor precedent.
       Chosen over an `IOptionsMonitor<GovernanceConfig>` scoped-decorator (heavier, same result).
    2. *Security depth* = **full adversarial pass** — security-reviewer subagent on the diff PLUS
       explicit bypass-attempt tests (casing/glob escape, auto-approve trying to lift a bypass-immune
       Deny, MCP name spoof, empty/null-envelope fail-closed), beyond the happy-path denials.
  - **Envelope-Deny mechanism (verified against the resolver):** a catch-all `*` Deny is unusable — Deny
    (Phase 1b) precedes Allow (Phase 3), so `*` Deny would also kill the *allowed* tools. Instead
    `EnvelopePermissionRuleProvider` computes the concrete out-of-envelope set
    `(overlay's requested tools) − envelope.AllowedTools` from the ambient overlay + ambient envelope
    and emits one bypass-immune Deny per out-of-envelope tool. Two-layer defense: envelope also joins
    the CM-PR2 construction-time intersection (`ToolPermissionFilter` / `BuildMergedToolsWithSourcesAsync`)
    so out-of-envelope tools aren't even built into the chain; the provider is the runtime backstop
    (also catches late-discovered MCP tools). MCP filter seam = `ToolChainBuilder` lines 54 + 218.
- **PR3 — Async job + handle lifecycle.** Job store, background service + queue, TTL handle cache +
  guaranteed temp-dir cleanup (post-eviction + idle sweeper).
- **PR4 — API surface.** `BundlesController`, middleware envelope capture, DI wiring, config section,
  `WebApplicationFactory` integration tests.
- **PR5 — Streaming opt-in.** SSE endpoint reusing AG-UI writer + sink.

## Full sequencing
Core-model pass (CM-PR1 → CM-PR4) FIRST, then the bundle API (Bundle-PR1 → Bundle-PR5). The bundle's
ingestion overlay (Bundle-PR1) reuses CM-PR3's agent-owned nested-skill discovery (a staged bundle is
an agent dir with nested skills), and the bundle's envelope (Bundle-PR2) sits on top of CM-PR2's tool
ceiling. Doing the core-model first is what makes the bundle drop in cleanly instead of bolting on.

## Decisions (resolved)
- **Host: DECIDED — new lean isolated `Presentation.BundleApi` web app.** Its own auth audience + rate
  limits; no shared front door with the dashboard, since it runs externally-authored agents.
- **Job records:** in-memory (non-durable), aligned with "not system of record". CONFIRMED.
- **Envelope granularity:** per-credential (subject) with role/default fallback. CONFIRMED.
- **CM-PR4:** delete only the test-only `AgentManifest` class; keep the workflow types. CORRECTED.
- **Sequencing:** Part 1 (agent promotion) is its own approval + ships standalone; Part 2 (bundle API)
  is a later decision on top of it.
