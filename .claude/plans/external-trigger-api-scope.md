# Scope: Trigger the harness from an API

**Status:** scoping only — nothing built, no decision taken.
**Date:** 2026-07-27 (v2 — adversarially verified, all five themes scoped)
**Goal (Matt):** "run all of the AI things we do within the harness by triggering it from an API."

Every factual claim below was either read directly from source or adversarially verified by an
independent agent instructed to refute it. Corrections from that pass are folded in; §9 logs them.

---

## 1. Where we actually stand (verified numbers)

The Application layer has **43 CQRS operations**. **16 are HTTP-reachable** (12 AgentHub,
4 BundleApi). **4** are callable only from ConsoleUI example code, **1** only from the EvalRunner
CLI, and **26** — including all 8 plan operations — have no host reference at all.

| Host | Surface | Trust boundary |
|---|---|---|
| `Presentation.AgentHub` | 40 routes — chat, conversations, documents, evals results, metrics, sessions, MCP proxy | Interactive / first-party |
| `Presentation.ExecutionApi` | 5 routes — register + run external agent bundles | **Untrusted external automation** |
| `Infrastructure.AI.MCPServer` | MCP transport, 3 skill-listing tools | MCP clients |
| `Presentation.FoundryHost` | Foundry Responses protocol, one hard-wired agent | Foundry runtime |

The through-line: capability after capability is built and tested but has no inbound path — the
same "inert machinery" pattern closed in waves 1–4, one layer up.

---

## 2. Track W — Workflows (the original ask)

### 2.1 What exists

A complete DAG engine (`Infrastructure.AI\Planner\`): `PlanExecutor` (4 partials), six step types
with keyed executors, checkpoint/resume via `EfCorePlanStateStore` (SQLite), structural validation
(cycles/reachability/branch completeness), sub-plans depth-capped at 5, 8 CQRS operations, OTel,
and a fully-defined AG-UI progress vocabulary.

### 2.2 Verified findings — stronger than first assessed

**The plan subsystem is 100 % inert.** Not "in-process only": *nothing* in any Presentation
project — including ConsoleUI — dispatches any plan operation or touches `IPlanExecutor`. The
engine's only live entry is its own tests.

**W-blocker: there is no production schema initialization for `PlannerDbContext`.** The codebase's
SQLite pattern (`SchemaInitializer<TContext>` / `EnsureCreated`) is registered for
`PromptUsageDbContext` and `EvalDashboardDbContext` but **never for PlannerDbContext**
(`DependencyInjection.Planner.cs`). The first production `SavePlanAsync` in any host fails with
"no such table". There are no EF Migrations anywhere in the repo. Consequences: the W-series must
add schema init; adding `OwnerId` later means adopting Migrations or create-fresh; the "backfill
existing rows" risk is moot — no production planner DB can exist today.

| # | Gap | Verdict | Notes |
|---|---|---|---|
| G1 | No OwnerId/TenantId on plans; nothing filters by identity | CONFIRMED | `PlanGraphEntity.cs` read in full; `grep OwnerId\|TenantId\|ScopeIdentity -- *Plan*` → none |
| G2 | Plan tool steps bypass the capability envelope | CONFIRMED, **refined** | See 2.3 — gap is `ToolUse` + `Retrieval` + `SubPlanInvocation`, NOT `LlmCall` |
| G3 | Retry config is dead — only `OnExhausted` read | CONFIRMED, **worse** | `RetryStepAsync` (manual) also never consults `MaxRetries` → unlimited manual retries |
| G4 | No inbound path | CONFIRMED | Stronger: no in-process caller either |
| G5 | Human gates unanswerable over HTTP | CONFIRMED | → Track H |
| G6 | No mid-flight unblock; resume = re-invoke `ExecuteAsync` | CONFIRMED | Documented follow-up at `PlanExecutor.Recovery.cs:100-102` |
| G7 | AG-UI plan progress wired, can never fire | CONFIRMED | |

### 2.3 The security picture, precisely

`LlmCallStepExecutor` delegates to `RunConversationCommand` → the full governed agent turn. That
path **is** envelope-aware when an envelope is ambient: `ToolChainBuilder` reads
`CapabilityEnvelopeAccessor.Current`, `ToolInvocationGovernor` gates on it. So a plan-run executor
that arms the envelope (BundleApi pattern) gets enforcement on `LlmCall` steps **for free**.

The bypass is the **sandbox path**: `ToolUseStepExecutor` calls only
`ICapabilityEnforcer.ResolveProfileAsync` (static per-tool profile) — never `EnforceAsync`, never
the governor, never the envelope. `Retrieval` calls the retriever directly. `SubPlanInvocation`
recurses and inherits the gap. **W2's scope is those three step types**, plus the single arming
site in the run executor.

Ship the endpoint without W2 and a workflow's `ToolUse` step is remote tool execution outside the
envelope — the exact confinement bundles have, absent.

### 2.4 PR sequence

| PR | Title | Content | Risk |
|---|---|---|---|
| **W0** | `fix: honour RetryPolicy in plan step execution` | Implement `MaxRetries`/`InitialDelay`/`BackoffStrategy`; bound `RetryStepAsync` or document it as operator-unlimited; interaction with step `Timeout` + at-least-once | Low |
| **W1** | `feat: planner schema init + plan ownership` | Register `SchemaInitializer<PlannerDbContext>`; add `OwnerId`+`TenantId` to `PlanGraphEntity`; stamp from `ScopeIdentity`; filter every read/list/execute. No backfill needed (no prod DB can exist) | Medium |
| **W2** | `feat: confine plan execution to the caller capability envelope` | Arm envelope in a new plan-run executor (single arming site, `IBundleRunExecutor` doctrine); route `ToolUse`/`Retrieval`/`SubPlanInvocation` through envelope checks; apply autonomy ceiling to `RequiredAutonomyLevel` | **High — security-review** |
| **W3** | `feat: workflow submission contract` | `WorkflowDefinition` DTO ↔ `PlanGraph` (polymorphic discriminators already exist in `StepConfiguration.cs`); FluentValidation; size/step/depth caps mirroring `BundleExecutionConfig`; `POST /api/workflows` → 201 | Medium |
| **W4** | `feat: workflow run + status (generalized job model)` | `POST /{id}/runs` → 202; `GET` status; `DELETE` cancel. **Generalize the bundle job store** (`TryBeginRun` CAS, dispatcher, TTLs) into a job-kind substrate — Tracks T and E become thin job registrations on it | Medium |
| **W5** | `feat: workflow progress streaming` | SSE over `AgUiPlanEvents`; per-caller concurrency cap; reservation TTL | Low |
| **W6** | `feat: human gates end-to-end` | Depends on H1. Answer a gate → resume the parked plan | Medium |

W0 is independent and can start now. W1+W2 precede W3–W5. (Verifier note: folding ownership into
W3 would be equally sound — kept separate for reviewability, not by necessity.)

---

## 3. The host decision (needs Matt) — now with a harder constraint

**New fact from Track H:** escalation and change-proposal state are **per-process, in-memory
singletons** (`DefaultEscalationService` ConcurrentDictionaries; only `InMemoryChangeProposalStore`
exists). An approve endpoint must live **in the same process** as the workload that raised the
escalation — a decision POSTed to one host cannot resolve an escalation raised in another.

### Option A — Extend BundleApi → rename `Presentation.ExecutionApi` *(recommended)*

Workflows + direct tool invocation + eval/optimization jobs join bundles on the one
untrusted-automation surface: one auth ladder, one caller-identity split, one envelope resolution,
one job substrate (W4), one SSE writer. HITL endpoints ship as a **shared controller mounted in
both** AgentHub (agent-turn escalations) and ExecutionApi (workflow gate escalations) — the
in-memory constraint makes per-host mounting mandatory anyway until H5 (durable store).

- Cost: rename churn (project, namespaces, `bundle-api.yaml`, onboarding ch17, README, CLAUDE.md, tests).
- A-without-rename is acceptable; a misleading name is smaller than a duplicated security boundary.

### Option B — New `Presentation.WorkflowApi` host
Third copy of auth/identity/SSE plumbing; two envelope-arming implementations to keep converged —
the exact divergence `IBundleRunExecutor.cs:20-27` exists to prevent. Not recommended.

### Option C — Add to AgentHub
Untrusted automation inside the interactive host, next to 40 first-party routes and a SignalR hub.
Wrong blast radius. Not recommended.

**Note:** Track K (knowledge) does NOT follow this decision — it belongs on **AgentHub**
regardless, because its security model is identity scope (`KnowledgeScopeMiddleware`, AgentHub-only
today), not capability envelopes. See §6.

---

## 4. Track H — Human-in-the-loop control plane

Full findings from code (all cited in the Theme-2 agent report; key facts):

- **Escalations have a real identity+authorization model already**: per-escalation approver
  rosters, AllOf/AnyOf/Quorum strategies, roster enforcement at
  `DefaultEscalationService.SubmitDecisionAsync:124`, `ApproverDecision.ApproverName` recorded.
  **The gap:** `ApproverName` is a caller-supplied string — over HTTP it must be stamped from
  token claims, never the body. Roster strings need a documented claim-mapping convention
  (recommend configurable claim, default `preferred_username`, OrdinalIgnoreCase).
- **Change proposals have NO roster** — `ReviewerId` is a required caller-supplied string; anyone
  who can dispatch approves as anyone. Role claim becomes the whole authorization; flag honestly.
- **Pending state does not survive restart** (documented "Phase 3+"). JSONL audit is durable,
  fail-closed (an approval that can't be audited is not reported resolved) — but recovery from it
  is not implemented.
- **Pre-existing defects found while scoping** (→ H0): `SubmitDecisionAsync` returns `null` for
  three distinct cases (unknown / non-roster / recorded-but-pending) — unmappable to HTTP;
  roster matching is `OrdinalIgnoreCase` on decide but **case-sensitive** on
  `GetPendingEscalationsAsync:194` — an approver with differing casing can decide but sees an
  empty pending list; status-guard failures use plain `Result.Fail` → would map to 500, need a
  `Conflict` failure type.
- **Loop closure exists in-process**: `TaskCompletionSource` waiter — an HTTP decision immediately
  releases a blocked agent turn in the same process. HumanGate plans are pull-based (G6).
- **Autonomy tiers are pure config** — read + side-effect-free decision preview is the entire
  honest surface. **Drift is push-based** (caller supplies dimension scores); there is no internal
  collector, so "trigger a scan" is not a supportable endpoint — and nothing in prod pushes
  evaluations today, so drift reads expose an empty system (same trap as KG query-without-build).

### Endpoints (BundleApi conventions throughout)

- `GET /api/escalations` (caller's roster items) · `GET /{id}` · `POST /{id}/decision {approve, reason}` · `POST /{id}/cancel` (admin)
- `GET /api/change-proposals?status=…` · `GET /{id}` · `POST /{id}/approve|reject|cancel`
- `GET /api/governance/autonomy/tiers/{subagentType}` · `POST /api/governance/autonomy/decision-preview`
- `GET /api/drift/baselines|history|audits` · `POST /api/drift/evaluations` + `/baselines/{…}/recalculate` (ops-role — evaluation-push is a history-poisoning vector)

Roles (following the `AgentHub.Traces.ReadAll` precedent): `Harness.Approvals.Decide`,
`Harness.Approvals.Admin`.

### PRs

| PR | Content | Depends | Risk | Sec review |
|---|---|---|---|---|
| **H0** | `fix:` discriminated result for `SubmitDecisionAsync`; case-sensitivity fix; `Conflict` failure type in `Result`/`MapFailure` | — | Low | No |
| **H1** | Escalation CQRS + controller; token-bound approver identity; roster-claim convention | H0 | **High — approval authz core** | **Yes** |
| **H2** | Change-proposal decision routes; `ReviewerId` from principal | H0 | Med-high | **Yes** |
| **H3** | Autonomy reads | — | Low | No |
| **H4** | Drift reads + role-gated writes | — | Medium | Writes only |
| **H5** | *(decision)* Durable escalation/proposal state (EF-backed) | H1 | Medium | No |

**Not building:** submit-proposal over HTTP (agent-side op needing ambient agent identity);
RunGate over HTTP (internal primitive); autonomy mutation (it's config); parameterless drift scan;
roster management API; webhooks (poll `GET /{id}`); per-proposal rosters (v1 = role + audited
`GateDecision` history); distributed state bus (H5 first if ever).

---

## 5. Track T — Direct tool invocation (+ delegation)

Key facts (verified in the Theme-3 report):

- **31 keyed `ITool` registrations** (26 direct + 5 connector-adapter). One uniform invocation
  shape: `ExecuteAsync(operation, parameters) → ToolResult`; `AIToolConverter` gives every tool
  the same `{operation, parametersJson}` schema → **one wire DTO covers all tools**.
- **No catalog exists and keyed DI cannot enumerate keys** → `GET /api/tools` needs a new
  `IToolCatalog` primitive fed by each tool DI file.
- **The governed path is 100 % reusable.** Arming = `CapabilityEnvelopeAccessor.Begin(resolve(User))`
  + `IAgentExecutionContext.Initialize` (synthetic identity — governor fails closed without it, by
  design) + governance accessors in try/finally → `AuthorizeAsync` → `ExecuteAsync`. Must live in
  one Application-layer `IDirectToolInvoker` (single-arming doctrine), not the controller.
- **Posture:** agent-path parity — in-process, self-sandboxing tools still self-sandbox, capability
  flags enforced. Not the plan-path sandbox (that's W2's world).
- **Output:** sanitize unconditionally (`ICompositeResponseSanitizer` + secret redaction +
  classification gate when configured). Do NOT apply tool-output compression — it's a
  context-window optimization that would hand callers summaries and unreachable references.
- **Overlap check, honestly:** a 1-step workflow *can* run a tool, but through plan persistence and
  202 ceremony. Unique value of direct invoke = synchronous call + the catalog (which W3 authors
  also need to write valid `ToolUse` steps). Verdict: build thin, after W4.
- **Delegation (Theme 6): mostly subsumed — defer.** `delegate_task` is itself a keyed tool: an
  envelope granting it gives bundle-run agents multi-agent delegation over the existing BundleApi
  today. `RunOrchestratedTaskCommand` is the weakest orchestration mechanism in the codebase
  (regex subtask parsing); freezing it into an external contract would be a mistake. Hard
  prerequisite anyway: the deferred per-sub-agent governance re-scoping (#176).
  Unique standalone value: read-only delegation telemetry over `IDelegationStore` (JSONL audit).

### PRs

| PR | Content | Depends | Risk | Sec review |
|---|---|---|---|---|
| **T1** | `IToolCatalog` + `GET /api/tools` (+`/{name}`), envelope-filtered (don't advertise what a caller can't call) | Host; after W3 | Low | Advisory |
| **T2** | `IDirectToolInvoker` + `POST /api/tools/{name}/invoke` — sync, sanitized, timeout-capped, audited via `GovernanceTrace` | T1 | **High — remote tool execution by design** | **Mandatory** |
| **T3** | Delegation telemetry reads (+ owner stamping on `DelegationRecord` — has `SupervisorId`, no caller owner) | Host; W1 pattern | Medium | Advisory |
| **T4** | *(only on explicit demand)* Orchestration-as-job on W4 substrate | W4 + #176 re-scoping | High | Mandatory |

**Not building:** T4 now; a parallel enforcement path; sandbox routing for direct invokes;
compression on external returns; per-tool DTOs; reworking `/api/mcp/tools/*` (docs note
distinguishing the two is enough). Docs must say "never grant `render_*`/`dashboard_control`/
`echo_*` in envelopes" — meaningless or demo-only over HTTP.

---

## 6. Track K — Knowledge operations (host: AgentHub)

Key facts (verified in the Theme-4 report):

- **Memory isolation works automatically on AgentHub**: `KnowledgeMemoryService` self-stamps
  owner/tenant from `IKnowledgeScope`; `KnowledgeScopeMiddleware` (AgentHub-only, `Program.cs:53`)
  sets it per request. A controller calling `IKnowledgeMemory` gets isolation with zero plumbing.
- **The write gate covers every `IKnowledgeMemory` writer** (registered unconditionally;
  quarantined facts persist but are never recalled). It does NOT cover `ILearningsStore`.
- **Exposing `RememberCommand` (learnings) over HTTP would bypass the write gate** — and learnings
  have **no owner/tenant scoping at all** (`LearningScope` = agent/team/global) and are **not
  covered by erasure** (zero references in `DefaultErasureOrchestrator`). An authenticated caller
  could inject a global "learning" recalled into *every user's* turns: prompt-injection-by-API,
  ungated, unscoped, un-erasable. **Learnings writes must not be exposed.**
- **Two physically separate graph families** — this reframes the dead-code question:
  - Family A (corpus): `IGraphDatabaseBackend` → Kuzu only, no tenant decorators, written by the
    dead `IndexCorpusAsync`, read by Global/LocalSearch. **This is the graph `/api/Documents/search
    Strategy=GraphRag` queries** — wiring `IndexCorpusAsync` into ingestion closes the
    empty-graph hole.
  - Family B (knowledge): `IKnowledgeGraphStore` (in-memory/postgres/neo4j) with
    tenant-isolation + compliance decorators, written by memory/learnings — and by the
    never-resolved `"kg-ingestion"` workflow, which would enrich the *memory* graph and do nothing
    for document search. The two dead build paths are NOT redundant; they build different graphs.
  - **Correction:** `LocalSearchAsync` is not dead — `GraphRetrievalSource` (keyed `"graph"`,
    Phase-D multi-source) calls it, config-gated. `IndexCorpusAsync` genuinely has zero callers.
  - **DI landmine:** `IGraphRagService` is registered unconditionally but its required
    `IGraphDatabaseBackend` only exists when `GraphDatabase.Enabled=true` AND provider=`kuzu` —
    other configs throw on first orchestrator resolution. Fix in K2.
- **Documents are deliberately shared** (memory-note correction: "docs isolated by tenant" was
  wrong — only memory is). The vector store is collection-aware, so opt-in per-tenant collections
  need no schema surgery — but caller-supplied `CollectionName` must then be rejected, else it's a
  cross-tenant read primitive. Erasure does not cover ingested documents (no owner to sweep by) —
  K4's stamping is the prerequisite for ever erasing them.
- HTTP-written memory inherits erasure for free (owner-stamped nodes on the decorated store).

### Endpoints

- `POST /api/memory {key, content, entityType?}` → returns the gate outcome honestly
  (persisted/quarantined/rejected) · `GET /api/memory/search?query=` · `DELETE /api/memory/{key}`
  (self-scoped by construction — the node id embeds the caller's scope)
- `GET /api/learnings?context=` — read-only, operator-role-gated (discloses cross-user learnings)
- KG build: config flag `IndexOnIngest` (default false) in `IngestDocumentCommandHandler` — the
  chunks exist only there; a standalone index endpoint would re-run parse/chunk. No re-index
  endpoint in v1.

### PRs

| PR | Content | Depends | Risk | Sec review |
|---|---|---|---|---|
| **K1** | Memory HTTP surface (remember/recall/forget CQRS + controller) | — | Low-Med | **Yes** |
| **K2** | Close KG build: `IndexOnIngest` stage; **retarget `"kg-ingestion"` as the memory-graph enrichment pipeline with a named consumer** (Matt decided: keep, not delete — a real caller must ship with it); fix the GraphRag DI landmine | — | Medium | No (full RAG test pass) |
| **K3** | Learnings recall, role-gated | Matt decision on role | Low | Yes |
| **K4** | Owner/tenant stamping at ingest + opt-in `ScopedCollections` (server-derived, client override rejected) | — | Med-High | **Yes** |

K1/K2 are independent of the W-series and the host decision — can run in parallel worktrees now.

**Not building:** harmonic-memory endpoints (internal representation strategy; rides the memory
surface automatically when enabled); `ImproveAsync` over HTTP (self-serve relevance-poisoning
primitive); learnings writes (triple-disqualified above); `RecordLearningAccessCommand`; generic
keyed-workflow trigger; admin arbitrary-owner erasure (already parked pending Matt).

---

## 7. Track E — Improvement loops

Key facts (verified in the Theme-5 report):

- **Eval execution**: suite definitions are in-repo YAML (`eval-datasets\`); each case is a full
  governed agent turn + LLM-judge calls; minutes-to-tens-of-minutes → async job mandatory. The
  handler returns exactly what `IngestEvalRunCommand` accepts — a server-side run dispatches ingest
  in-process, deleting the CLI's HTTP hop. **Hazards:** the composition root registers
  `NotConfiguredEvalRunner` (only the EvalRunner CLI calls `AddEvaluationDependencies()`); the
  validator has **no upper bound on Parallelism** (the 1–128 cap is CLI-only); raw `DatasetPaths`
  on the wire = arbitrary-file-read probe → names-only against an allowlisted root.
- **Skill training: don't build.** `NotConfiguredPatchProposer`/`NotConfiguredRolloutRunner` are
  the only registrations — an endpoint would 500 on every call in every host. Even ConsoleUI uses
  deterministic stubs. Prerequisite is a separate feature (agent-backed impls + durable checkpoint
  store). The 4 other skill-training ops are loop internals — never expose.
- **Meta-harness optimization works out-of-box** but is the costliest (hours; agent runs ×
  iterations), writes only under `TraceDirectoryRoot` (advisory `_proposed\`, never live config).
  Defer to third application of the job pattern.
- **Cross-cutting: no budget mechanism protects any server-triggered loop.**
  `IConversationBudgetTracker` guards conversation loops only ("between turns, never mid-turn") —
  eval cases are single independent turns and never enter that path. Server-side config caps
  (MaxCases/MaxRepeats/MaxParallelism) are the minimum viable cost control and gate E2 the way
  W1/W2 gate W3.

### PRs

| PR | Content | Depends | Risk | Sec review |
|---|---|---|---|---|
| **E1** | Server-side eval enablement: `AddEvaluationDependencies()` in host; dataset-**name** resolver vs allowlisted root; config caps | Host decision | Medium | **Yes** (path resolution) |
| **E2** | `POST /api/evals/runs` → 202 job on W4 substrate; status/cancel; in-process ingest on completion; role `Harness.Evals.Execute` | **W4** + E1 | Med-high (remote LLM spend) | **Yes** |
| **E3** | *(deferred)* Optimization runs as jobs; never echo `ProposedChangesPath` | E2 proven | High cost | Yes |

**Not building:** skill-training endpoint (guaranteed 500); epoch-boundary commands
(`SlowUpdate`/`MetaSkillUpdate` — handler-internal, exposing them bypasses run audit);
`ReflectOnFailures`/`GateCandidateSkill`; raw paths on the wire; synchronous variants; E3 now.

---

## 8. Master sequencing + decisions needed from Matt

### Decisions — ALL DECIDED by Matt, 2026-07-27

1. **Host**: ✅ **Option A — extend BundleApi and rename to `Presentation.ExecutionApi`.**
2. **`"kg-ingestion"` workflow**: ✅ **Keep and retarget** (against the delete recommendation) —
   re-purpose as the memory-graph enrichment pipeline **with a named consumer wired in**. K2's
   scope grows accordingly: it must ship a real caller, not just a relabel.
3. **Learnings recall exposure** (K3): ✅ **Expose read-only behind an operator role.**
4. **K4 posture**: ✅ **Both** — provenance stamping AND opt-in `ScopedCollections` in one PR.
5. **H5**: ✅ **Build durable escalation/proposal state** as part of Track H (after H1).

### Sequence

```
Wave 1 (now, independent):    W0 · H0 · K1 · K2      (4 parallel worktrees)
Wave 2 (host = ExecutionApi): W1 → W2 (sec-review) → W3 → W4 → W5
  in parallel after H0:       H1 (sec-review) → H5 · H2 · H3 · H4
  in parallel (AgentHub):     K3 · K4 (sec-review)
Wave 3 (after W4 substrate):  E1 → E2 · T1 → T2 (sec-review) · W6 (needs H1)
On demand only:               T3 · T4 · E3
```

Rationale: W4's generalized job model is deliberately the keystone — eval runs, tool catalogs,
and any future long-running trigger become thin job-kind registrations instead of third and fourth
copies of the CAS/dispatcher/TTL plumbing.

### Standing cross-cutting rules (apply to every PR above)

- Identity is stamped from the token at the transport boundary, never accepted from the body
  (`ApproverName`, `ReviewerId`, `OwnerId` — all of them).
- One arming site per execution path (`IBundleRunExecutor` doctrine).
- Ownership mismatch → 404 (delete → 204 no-op), never 403; 403 is the kill-switch only.
- Feature-gate in handlers via `IOptionsMonitor`, DI registration stays unconditional and passive.
- Fail-closed auth ladder with startup throw; anonymous requires explicit opt-in + hosted-service warning.
- Sanitize all tool/agent output leaving the trust boundary, unconditionally.

---

## 9. Verification log (adversarial pass, 2026-07-27)

An independent agent attempted to refute every claim in v1. Results:

- **Confirmed:** G1, G2 (refined), G3 (worse), G4 (stronger), escalation gap, MCP false friend,
  unauthenticated `/health/ai` + HealthChecks-UI, Documents shared namespace, `IndexCorpusAsync`
  dead, `"kg-ingestion"` never resolved.
- **Corrected:** 44 → **43** CQRS ops (3 regex hits were doc-comment examples); "6
  ConsoleUI-exclusive" → **4** (+1 EvalRunner-CLI); **`LocalSearchAsync` is NOT dead** —
  `GraphRetrievalSource` calls it (empty-graph flag gets *stronger*, since a reachable read path
  hits an empty graph); "plans creatable by in-process code today" → nothing creates them at all.
- **Missed in v1, now folded in:** PlannerDbContext has no production schema initialization
  (→ W1); G2 is three step types, not the whole engine (→ W2 scope); manual retry is unbounded
  (→ W0); the HITL in-memory co-residency constraint (→ §3); the eval Parallelism cap is CLI-only
  (→ E1); learnings are un-erasable (→ K-track do-not-build).
