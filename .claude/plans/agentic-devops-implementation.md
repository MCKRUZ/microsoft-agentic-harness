# Agentic DevOps — Harness Implementation Plan

> **Source:** `documentation/reference/agentic-devops-findings.md` (compiled 2026-06-04, 6-track research pass, ~55 primary sources).
> **Status:** Plan only. No code yet. Requires user approval per work item before implementation begins.
> **Author:** Claude, 2026-06-05.
> **Revised:** 2026-06-05 through the global "Optimize for Best Outcome, Not Speed" rule. Speed-coded recommendations (defer-A2A, Bicep-only, informational-eval-first, PR-size caps as goals) removed.
> **Deepened:** 2026-06-05 — Task #6 PR-3 prerequisite + tenant-ambient integration into PR-1; PR-2 design depth; PR-3 SSRF survey criteria.
> **Corrected:** 2026-06-05 — after grounding PR-1 against the merged Task #6 code, the "extend the tenant ambient" framing was wrong. Task #6 deliberately separates **human-caller scope** (`KnowledgeScopeAccessor`, AsyncLocal, `{user, tenant, dataset}`) from **agent-execution scope** (`IAgentExecutionContext`, scoped DI, `{AgentId, ConversationId, TurnNumber}`). Agent identity belongs with the agent-execution scope, not the human-caller scope. PR-1 detail rewritten to reflect this. Memory namespacing stays `memory:{tenant}:{user}:{key}` — agent identity is a separate RBAC axis, not a memory-key dimension.
> **Related plans:** `.claude/plans/maf-native-adoption-plan.md`, `.claude/plans/task6-multitenant-memory-isolation.md`.
>
> **Prerequisite — SATISFIED 2026-06-05:** Task #6 PR-3 merged to `main` (PR #20 + PR #23). The tenant ambient (`KnowledgeScopeMiddleware` / `KnowledgeScopeHubFilter` + `AsyncLocal`) is now stable. PR-1's `IAgentIdentity` ambient extends it per §5 PR-1 detail. **PR-1 is unblocked and ready to start.**

---

## 1. Purpose & Framing

The findings doc is a **fact base, not a directive**. It surfaces 10 open decision points (section 8) and 6 convergent patterns (section 7) from how the industry built agentic DevOps platforms through mid-2026. This plan does three things:

1. **Map** each finding to existing harness capability (have / partial / gap).
2. **Decide** which gaps the harness should close to stay a credible "production template for an agentic DevOps system" — and which it should explicitly punt on.
3. **Sequence** the work into reviewable PRs, each independently shippable, each landing behind a feature flag where it touches an existing runtime path. PRs are sized to the natural seam of the abstraction, not to a LOC cap — a load-bearing artifact (e.g. `ChangeProposal`) ships as one coherent PR even if it's large, because splitting it would force compat shims that violate "Replace, don't deprecate".

The harness is already MAF-native, has Skills/Plugins/MCP/Tools/RAG/KG/Governance/SkillTraining. The convergent industry patterns map cleanly onto subsystems we already own. This plan is mostly **wiring + new skill packs + a few new abstractions**, not a rewrite.

---

## 2. Findings → Harness State Map

Per the 6 convergent patterns from section 7 of the findings:

| Pattern (industry convergence) | Harness state | Gap |
|---|---|---|
| **Agent proposes, gate disposes** (artifact + required checks + approval) | PARTIAL — `EscalationTracker`, `AutonomyTierRuleProvider`, `AutonomyTierPolicy` exist. No first-class "Change Proposal" artifact type. | Need `ChangeProposal` domain model + gate pipeline + per-tier approval routing. |
| **Graded autonomy** (Review → Autonomous, tuned per blast radius) | PARTIAL — Manual / Supervised / Autonomous tiers exist, applied as a MediatR pipeline behavior. | No per-action / per-blast-radius tuning. No Azure-SRE-style **Incident-Response-Plan** abstraction that scopes autonomy per scenario. |
| **Identity + RBAC as the boundary** | GAP — no agent-identity abstraction. Tools resolve via keyed DI; nothing carries an `IAgentIdentity` through tool execution. | Need `IAgentIdentity` + `IAgentCredentialProvider` (Entra Agent ID shape, federated/MI/cert/secret hierarchy) and tool-level RBAC check. |
| **Mechanical self-validation** | PARTIAL — `PlanExecutor` runs steps; tests/lint/policy as gate tools not formalised. | Need a `SelfValidationStage` that runs declared validators (test/lint/policy) on a `ChangeProposal` and rejects the artifact mechanically before any human is asked. |
| **Bounded tool surface** | STRONG — Skills + AllowedTools/DeniedTools + keyed DI is already the bounded-surface story. | Mostly fine. Need a Dagger-style **Workspace** skill that bundles `read/write/list/test/lint` against a sandboxed working copy, as a canonical example. |
| **Containment-first** (sandbox + default-deny egress, before model judgment) | PARTIAL — `Sandbox*` domain types exist. No evidence of `ProcessSandboxExecutor` / `DockerSandboxExecutor` actually wired (need to verify). Egress policy not modelled. | Need to verify sandbox executors exist or build them, plus a `IEgressPolicy` abstraction (default-deny + allowlist) returning a Polly handler / `HttpClient` factory. |

Per the 10 open decision points (section 8):

| # | Decision | Current harness disposition | Proposed direction |
|---|---|---|---|
| 1 | **Orchestration substrate** (MAF Agent vs Workflow + Magentic) | MAF-native adoption already chosen. | **Align with MAF Magentic** for multi-step ops work; expose the ledger via OTel; keep current single-agent CQRS path for everything that doesn't need replan. |
| 2 | **Single- vs multi-agent for ops** | Single-agent today with delegation tool (`CapabilityMatchSupervisor`). | **Hybrid:** single coherent context (Cognition) by default; orchestrator-worker (Anthropic / MAF Magentic) only when the task graph crosses the MAST failure-mode threshold (long horizon, parallel subtasks, condensed results). Decision lives on the skill, not globally. |
| 3 | **Autonomy model** | Manual / Supervised / Autonomous tiers exist. | Adopt a **two-tier per-skill autonomy model (Review / Autonomous)** as a refinement of the existing 3-tier, scoped per skill *and* per blast-radius. Default for state-changing actions = Review. |
| 4 | **Change-application contract** | No `ChangeProposal` artifact today. | Build it. See PR-2. |
| 5 | **Tool integration** | MCP server + client exist; tools keyed DI; tool output compression in pipeline. | Add a documented **MCP trust/scoping policy** doc + the `code-execution-with-MCP` pattern as an optional skill (Anthropic-style, to limit token bloat across large tool surfaces). |
| 6 | **Identity** | GAP. | Build `IAgentIdentity` + `IAgentCredentialProvider`. See PR-3. |
| 7 | **Observability — OTel GenAI semconv** | Already adopted per `OTel Blueprint` memory; `GenAiSemconvRegistry.cs` is single source of truth. | Pin via `OTEL_SEMCONV_STABILITY_OPT_IN`. Add **content-capture policy** (off by default; PII redaction). Add the Magentic ledger as spans. |
| 8 | **Threat governance** | Security audit done (`project_security_audit`). | Add **OWASP Agentic Top 10 control map** as a documentation artifact + a CI eval pack that exercises each row. |
| 9 | **Memory** | KG + cross-session memory + tenant isolation already shipped/shipping. | **No new abstraction needed**. Add a *retrieval-feedback* skill that writes resolution outcomes back into `IKnowledgeMemory`, matching Azure SRE Agent + GitOps reference. |
| 10 | **Standards bets — MCP + A2A** | MCP committed. A2A not yet exposed. | Expose **A2A surface via MAF** (A2A is native in MAF per findings). See PR-7. |

---

## 3. Guiding Principles (distilled, not invented)

These are the principles the plan optimises for. They are the convergences from section 7 + the constraints from the harness CLAUDE.md, stated tightly so every PR can be checked against them:

1. **Every mutating action is an artifact** (`ChangeProposal`) before it is an effect. The artifact is the unit of review, the unit of self-validation, the unit of audit.
2. **The artifact's gate is mechanical first, human second.** Tests / lint / policy / dry-run run from the environment; the human sees a green or red gate, not raw output.
3. **The agent's runtime identity is the durable boundary.** Prompt-level rules degrade; RBAC does not. Every tool resolves an `IAgentIdentity` and checks scope.
4. **Containment before judgment.** Sandbox + default-deny egress before any model-side guardrail.
5. **Autonomy is graded by blast radius, not globally.** Per-skill, per-action, per-environment. Default Review for state-changers.
6. **Skills are how the surface grows.** A new ops capability ships as a skill pack (plus its MCP server if external). Not as a new top-level subsystem.
7. **Failure feeds memory.** Every resolution writes back into KG memory with provenance and feedback weight. Drift detection consumes that signal.

---

## 4. Phased Roadmap (PR-sized work items)

Twelve PRs across four phases. Each PR is independently mergeable, ships behind a config flag when it touches a runtime path, and has acceptance gates. Estimated total: ~6–8 weeks of focused work, depending on which PRs are de-scoped after section-8 user review.

### Phase A — Foundations (must land before anything else uses them)

- **PR-1: Agent Identity** — `IAgentIdentity`, `IAgentCredentialProvider`, credential hierarchy (Entra-Agent-ID-shaped: federated / managed identity / cert / secret), `AsyncLocal<IAgentIdentity>` ambient resolution, tool-execution check.
- **PR-2: ChangeProposal artifact + gate pipeline** — `ChangeProposal` domain model (mirrors PR/MR semantics), `IChangeProposalGate` (interface), `SelfValidationStage`, `ApprovalStage` (routes to existing `EscalationTracker`), `MergeStage` (the only mutator). Audit via JSONL audit store already in place.
- **PR-3: Egress policy + sandbox audit** — verify/finish `ProcessSandboxExecutor` and `DockerSandboxExecutor` are present and wired; add `IEgressPolicy` (default-deny + per-skill allowlist) returning an `HttpClient` factory; tools that need network get one through DI, not directly.

### Phase B — Autonomy & Orchestration

- **PR-4: Graded autonomy refinement** — extend `AutonomyTierPolicy` with `BlastRadius` (Trivial / Low / Medium / High / Critical) + per-skill + per-environment override. Default-Review-for-state-changers enforced at the `ChangeProposal` gate.
- **PR-5: Incident-Response-Plan abstraction** — `IncidentResponsePlan` config object that maps incident type → skill set → autonomy override. Loaded at startup from `appsettings.json`, hot-reloadable via `IOptionsMonitor`. Mirrors Azure SRE Agent's tuning model.
- **PR-6: Magentic orchestration surface** — wrap MAF Magentic behind an `IMagenticOrchestrator` skill that emits OTel spans for outer Task Ledger + inner Progress Ledger + stall counter + replan events. Reusable across DevOps skill packs.

### Phase C — DevOps Skill Packs (the actual agentic-DevOps surface)

- **PR-7: A2A surface + cross-agent contract** — expose A2A via MAF as the inter-agent protocol from the outset, so every inter-skill call across PR-8/PR-9/PR-10 uses the same wire shape as cross-process. Document inter-agent message contract; the SRE↔Workspace path is the first concrete consumer. Lands before PR-8 because PR-8 is its first consumer.
- **PR-8: Workspace skill pack (Dagger-style)** — bounded `read/write/list/runTests/runLint` against a sandboxed working copy; ships as a skill with AllowedTools = those five, DeniedTools = shell/raw-fs. Canonical example for "bounded tool surface". The self-validation reference implementation. Talks to other skills over A2A (PR-7).
- **PR-9: GitOps skill pack (Flux + ArgoCD)** — port the spirit of `fluxcd/agent-skills` (`gitops-knowledge`, `gitops-repo-audit`, `gitops-cluster-debug`) as harness skills, AND ship ArgoCD parity at the same time. Two GitOps controllers cover the bulk of the enterprise install base; shipping only one forces consumers to write the other themselves, which is exactly the corner-cutting the Quality Bar forbids. Mutator is always a `ChangeProposal` against the GitOps repo, never a `kubectl apply`. Skill carries DeniedTools = `kubectl_apply`, `kubectl_delete`, `helm_upgrade`.
- **PR-10: IaC skill pack (Terraform + Bicep, parity)** — both generators ship together. Each produces, plans (`terraform plan` / `bicep build`), and scans (Checkov + tfsec for TF; Checkov + ARM-TTK for Bicep) against the same `ChangeProposal` gate. Skill never deploys; deployment is a separate `ChangeProposal` flagged `BlastRadius=Critical` and gated by both autonomy override and human approval. Reasoning for parity: the harness is multi-cloud-capable; shipping Bicep alone implicitly Azure-locks the template.

### Phase D — Cross-Cutting Hardening

- **PR-11: OTel GenAI content-capture policy** — pin `OTEL_SEMCONV_STABILITY_OPT_IN`, add content-capture toggle (off by default), PII redaction filter. Magentic ledger spans land here. Updates the OTel Blueprint doc.
- **PR-12: OWASP Agentic Top 10 control map + eval pack** — documentation artifact mapping each row to harness control(s), plus an eval pack (using existing eval framework) that exercises Goal Hijack, Tool Misuse, Memory Poisoning, Insecure Inter-Agent Comm, Rogue Agents. Lands as `documentation/security/owasp-agentic-top-10.md` + eval fixtures.

---

## 5. Per-PR Detail

Each subsection lists: **deliverable / files touched / depends on / risk / acceptance**.

### PR-1: Agent Identity

- **Deliverable:** `IAgentIdentity`, `IAgentCredentialProvider`, `AmbientAgentIdentity` (`AsyncLocal` carrier), `AgentIdentityResolutionMiddleware` (Presentation), credential-hierarchy ordering (federated → managed identity → cert → secret), RBAC check helper, Entra Agent ID wiring for the federated and managed-identity paths.
- **Integration with the two existing identity scopes (corrected after grounding):** Task #6 deliberately maintains two separate scopes. PR-1 respects that separation:
  - **Human-caller scope** (`KnowledgeScopeAccessor`, `AsyncLocal`, `{user, tenant, dataset}`) — DO NOT TOUCH. Established by `KnowledgeScopeMiddleware` / `KnowledgeScopeHubFilter` from `ClaimsPrincipal`. Its purpose is memory-namespacing and tenant isolation for the human who initiated the work.
  - **Agent-execution scope** (`IAgentExecutionContext`, scoped DI, `{AgentId, ConversationId, TurnNumber}`) — EXTEND this. Add `IAgentIdentity? AgentIdentity { get; }` to the interface. `AgentContextPropagationBehavior` (the existing MediatR behavior that initializes the scope) resolves the agent's identity from `IAgentCredentialProvider` and stamps it onto the context at scope initialization.
  - **`AgentFactory` integration:** the existing factory already creates agents from an `AgentExecutionContext`; this PR adds the identity resolution step inside the factory's construction path so every agent ships with a resolved identity.
  - **Memory namespacing stays as-is** (`memory:{tenant}:{user}:{key}`). Adding agent identity to the key would fragment a user's memory across every agent they ever invoke, which defeats the cross-session memory model.
  - **RBAC reads from BOTH scopes at tool-call time.** A tool-call check reads the human-caller scope (who initiated → does this user have access to this resource?) AND the agent-execution scope's identity (which agent acted → is this agent allowed this tool?). Two independent decisions, AND-ed.
  - **New `IAgentIdentityValidator`** for the agent-side RBAC check, registered alongside (not extending) `IKnowledgeScopeValidator`.
- **Credential hierarchy detail:**
  - **Federated credential** (workload identity / OIDC) — preferred for cloud runtimes. No stored secret. Issuer is per-deployment (GitHub Actions OIDC, Azure DevOps OIDC, AKS workload identity, etc.) — design time selects which to ship as the canonical worked example based on Q2's answer.
  - **Managed identity** — for Azure-hosted runtimes. Azure auto-rotates; no stored secret.
  - **Certificate** — for hybrid scenarios.
  - **Client secret** — explicitly last-resort; logs a warning at startup if used outside Development.
  - Resolution order is fixed; providers cannot be reordered by config. A consumer can register fewer (e.g. only federated) but the order of those registered is invariant.
- **Files (corrected after grounding):**
  - new `Domain.AI/Identity/` — `IAgentIdentity`, `AgentIdentityKind` enum, `CredentialContext`, supporting records (Domain, no framework refs)
  - new `Application.AI.Common/Interfaces/Identity/` — `IAgentCredentialProvider`, `IAgentIdentityResolver`, `IAgentIdentityValidator`
  - **extended:** `Application.AI.Common/Interfaces/Agent/IAgentExecutionContext.cs` — add `IAgentIdentity? AgentIdentity { get; }`; extend `Initialize(...)` overload or add separate `SetIdentity(...)` method
  - **extended:** `Application.AI.Common/Services/Agent/AgentExecutionContext.cs` — store the identity, validate scope-leak on re-initialization (same agent identity required)
  - **extended:** `Application.AI.Common/Factories/AgentFactory.cs` — resolve identity from `IAgentCredentialProvider` during agent construction, stamp onto context
  - **extended:** existing `AgentContextPropagationBehavior` (MediatR behavior) — if it currently initializes only AgentId/ConversationId, extend to also stamp identity
  - new `Infrastructure.AI/Identity/` — `FederatedCredentialProvider`, `ManagedIdentityCredentialProvider`, `CertificateCredentialProvider`, `ClientSecretCredentialProvider`, `DevelopmentAgentCredentialProvider` (test/dev only), `EntraAgentIdResolver`
  - new `Domain.Common/Config/AI/Identity/AgentIdentityConfig.cs` — `AppConfig.AI.Identity.Enabled`, credential-hierarchy config
  - DI wiring in each layer's `DependencyInjection.cs`
  - NO changes to `KnowledgeScopeMiddleware`, `KnowledgeScopeHubFilter`, `KnowledgeScopeAccessor`, `IKnowledgeScopeValidator`, or the human-caller `AsyncLocal`
- **Depends on:** nothing in this plan; depends on existing `IAgentExecutionContext`, `AgentFactory`, `AgentContextPropagationBehavior`.
- **Risk:** **Medium** (down from High after correction) — touches every tool's execution path, but does NOT modify the Task #6 ambient that just stabilised. Mitigation:
  - opt-in flag `AppConfig.AI.Identity.Enabled` — when false, `IAgentExecutionContext.AgentIdentity` is always `null`, tools skip the agent-side RBAC check, behaves exactly as today
  - integration tests that run with flag both off and on, against the same Task #6 fixtures, asserting no regression of tenant isolation
- **Acceptance:**
  - unit tests on each `IAgentCredentialProvider` implementation
  - unit tests on `IAgentIdentityValidator` (per-tool RBAC matrix)
  - integration test: agent execution within a request flows both identities (human-caller scope from `KnowledgeScopeAccessor`, agent identity from `IAgentExecutionContext`) into a tool call simultaneously
  - integration test: agent identity flows into sub-agent execution and post-turn background continuations
  - integration test: RBAC check rejects an unauthorised tool call
  - integration test: Task #6 tenant-isolation tests stay green (no regression)
  - flag false → zero behaviour change vs Task #6's post-merge state
  - flag true → identity stamped on every tool span in OTel
  - build + full suite green

### PR-2: ChangeProposal Artifact + Gate Pipeline

- **Domain model — `ChangeProposal`:**
  - `Id` (deterministic — hash of `{target, diff, submittedBy, submittedAt-bucket}` so re-submission of the same change is idempotent)
  - `Summary` (human-readable, agent-generated, capped at ~500 chars)
  - `Target` (typed — `GitRepoTarget`, `KubernetesResourceTarget`, `IacDeploymentTarget`, etc. — extensible via DI keyed registration like step executors)
  - `Diff` (structured, not text — uses the same `Edit{Op,Target,Content}` shape as SkillTraining's `PatchApplier` so the harness has one diff representation; supports `Append`, `InsertAfter`, `Replace`, `Delete`)
  - `BlastRadius` (Trivial / Low / Medium / High / Critical — feeds PR-4's autonomy decision)
  - `RequiredGates` (ordered list of gate keys — defaults derived from `Target` type + `BlastRadius` via `IChangeProposalGateResolver`)
  - `Status` (state machine: `Draft → Validating → AwaitingApproval → Approved → Merging → Merged` plus terminal `Rejected` and `Cancelled`)
  - `SubmittedBy` (`IAgentIdentity` from PR-1's ambient at submission time — captured, not re-resolved later)
  - `History` (append-only list of gate decisions with timestamp, gate key, decision, payload hash, optional reviewer if `ApprovalGate`)

- **Gate interface:**
  - `IChangeProposalGate` — `string Key { get; }`, `Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext ctx, CancellationToken ct)`
  - `GateResult` is a sum type: `Pass`, `Fail(reason, evidence)`, `Defer(reason, retryAfter)`. No `Skip` — gates are either required or not registered.
  - Each gate is stateless; all state lives on the `ChangeProposal`.
  - Gates are registered keyed (`AddKeyedTransient<IChangeProposalGate>("self_validation", …)`) and resolved at runtime by `RequiredGates`.

- **Built-in gates (this PR ships all four):**
  - **`SelfValidationGate`** — runs declared validators on the target. For `GitRepoTarget`, the validators are `run_tests` + `run_lint` (provided by the Workspace skill in PR-8 — until PR-8 lands, fixture validators substitute for tests). For `KubernetesResourceTarget`, `kubectl --dry-run=server`. For `IacDeploymentTarget`, the IaC backend's plan command. Validators are themselves keyed-DI services so consumers add their own without forking the gate.
  - **`PolicyGate`** — evaluates declared policies (Checkov / OPA / Kyverno / consumer-defined). Each policy is a keyed `IChangeProposalPolicy`. Policies output structured findings; the gate maps findings → `Pass` or `Fail` based on severity threshold from config.
  - **`ApprovalGate`** — routes to the existing `EscalationTracker` using its `AllOf` / `AnyOf` / `Quorum` workflows. The autonomy decision from PR-4 + PR-5 determines whether `ApprovalGate` short-circuits (Autonomous tier + low blast radius → auto-approve with audit entry) or enqueues a human approval. Even in auto-approve, the audit trail records the auto-approval explicitly so it's distinguishable from a real human nod.
  - **`MergeGate`** — the **only mutator in the entire pipeline**. Applies the diff to the target via the target's `IChangeApplier` (e.g. `GitChangeApplier` does `git commit` + `git push`; `KubernetesChangeApplier` writes the YAML to the GitOps repo, never `kubectl apply`). After merge, emits `ChangeProposalMerged` domain event for downstream consumers (drift detection baseline update, learnings log capture, KG memory feedback write).

- **Orchestrator:**
  - `ChangeProposalOrchestrator` runs gates sequentially in `RequiredGates` order. First `Fail` terminates → `Rejected`. `Defer` parks the proposal with a scheduled retry. `Pass` advances to the next gate.
  - On any gate exception (not a `Fail`, an unhandled throw): proposal moves to `Rejected` with the exception logged; never partial-merge.
  - Idempotency: re-submitting the same `ChangeProposal.Id` is a no-op if a non-terminal status already exists; if terminal, returns the prior result.

- **Audit shape (one JSONL line per gate decision):**
  ```json
  {
    "timestamp": "2026-06-05T14:23:11.123Z",
    "proposalId": "...",
    "gateKey": "self_validation",
    "decision": "Pass" | "Fail" | "Defer",
    "reason": "...",
    "evidenceHash": "sha256:...",
    "agentIdentity": { "tenant": "...", "user": "...", "agent": "..." },
    "blastRadius": "Medium",
    "durationMs": 12345
  }
  ```
  Lines are appended to the existing JSONL audit store. Evidence (full validator output, policy findings) is stored content-addressed under `evidenceHash` so audit lines stay small but full evidence is recoverable.

- **Shadow mode:**
  - `ChangeProposalOrchestrator` runs in two modes: `Live` (gates evaluate, terminal status drives effects) and `Shadow` (gates evaluate, decisions audit-logged, no effects). Shadow mode lets a consumer run the pipeline against existing skills before flipping `Live` per skill, validating the gate behaviour against real proposals first.

- **CQRS surface:**
  - `SubmitChangeProposalCommand` — agent calls this; returns proposal id + initial status
  - `RunGateCommand` — orchestrator dispatches per gate (internal)
  - `ApproveChangeProposalCommand` — external (human) approval handler
  - `RejectChangeProposalCommand` — external rejection
  - `CancelChangeProposalCommand` — agent or human cancellation
  - `GetChangeProposalQuery` — read by id
  - `ListChangeProposalsQuery` — read by status / tenant / agent

- **Files:**
  - new `Domain.AI/Changes/` — `ChangeProposal`, `ChangeProposalStatus`, `BlastRadius`, `GateResult`, target types
  - new `Application.AI.Common/CQRS/Changes/` — commands, queries, handlers, validators
  - new `Application.AI.Common/Interfaces/Changes/` — `IChangeProposalGate`, `IChangeProposalPolicy`, `IChangeApplier`, `IChangeProposalGateResolver`
  - new `Infrastructure.AI/Changes/` — `ChangeProposalOrchestrator` + four built-in gates + base `IChangeApplier` impls
  - DI keyed registration in `Infrastructure.AI/DependencyInjection.cs`
  - integration with `EscalationTracker` for `ApprovalGate` routing

- **Depends on:** PR-1 (each gate execution carries the agent identity from the ambient).

- **Risk:** **Medium-High** — new central abstraction. Mitigation: ships behind `AppConfig.AI.Changes.Enabled` (default off); Shadow mode is the default-on sub-flag once Enabled flips; consumers manually flip to Live per skill after watching shadow audits.

- **Acceptance:**
  - unit tests on each gate
  - unit tests on the state machine (every transition + every illegal transition rejected)
  - integration test of full proposal lifecycle Draft → Merged
  - integration test of every terminal failure path (Validating-Fail, Approval-Reject, Merging-Fail)
  - idempotency test (re-submit same id → same result, no duplicate gates)
  - shadow-mode test (proposals processed but no effect on `IChangeApplier`)
  - JSONL audit captures every gate decision with the expected shape
  - flag off → zero behaviour change in the codebase
  - flag on, no skill opted in → still zero behaviour change
  - build + full suite green

### PR-3: Egress Policy + Sandbox Completion

- **Deliverable:** Sandbox executors completed and verified (`ProcessSandboxExecutor` + `DockerSandboxExecutor`); `IEgressPolicy` + `EgressPolicyHttpClientFactory`; SSRF defense layered into the factory via a `DelegatingHandler`. Default policy = deny-all. Per-skill allowlists declared in skill manifest. Egress decisions audit-logged through the existing JSONL audit store.

- **SSRF defense — threat model the implementation must address:**
  - Block requests to **RFC 1918 private ranges** (10/8, 172.16/12, 192.168/16) and their IPv6 equivalents (`fc00::/7`).
  - Block **link-local** (169.254/16, `fe80::/10`).
  - Block **loopback** (127/8, `::1`).
  - Block **cloud metadata service IPs** — `169.254.169.254` (AWS / Azure IMDS / GCP), `fd00:ec2::254` (AWS IPv6 IMDS).
  - Block **DNS rebinding** — check resolved IP *after* DNS resolution, not just the hostname. A hostname that resolves to a public IP at filter time but a private IP at connect time is the classic bypass; the filter must hook at socket-connect or use a `SocketsHttpHandler` with custom `ConnectCallback`.
  - Block **redirects to forbidden IPs** — every redirect target is re-validated, not just the original URL.
  - Block **non-HTTP schemes** — `file://`, `gopher://`, `ftp://`, etc. Only `https` and (config-gated) `http` allowed.

- **SSRF library survey (executed 2026-06-05):**
  - **Full survey + threat model + integration sketch** → `documentation/security/ssrf-defense.md`
  - **Verdict: ADOPT `Microsoft.Security.AntiSSRF`** (NuGet: `Microsoft.Security.AntiSSRF`, source: `microsoft/AntiSSRF`, MIT, last commit 2026-05-29).
  - **Why this won the survey:**
    - Uses `SocketsHttpHandler.ConnectCallback` for **post-resolve IP filtering** — true DNS-rebinding defense, not pre-resolve hostname check
    - Disables `HttpClient.AllowAutoRedirect` and runs a custom `RedirectHandler` so every redirect target is re-validated against the policy
    - Ships an `IPAddressRanges` catalogue covering RFC 1918, loopback, link-local, multicast, and explicit IMDS (`169.254.169.254/32`)
    - `AntiSSRFPolicy` exposes CIDR allowlist + denylist + `DenyAllUnspecifiedIPs` (default-deny) + `AllowPlainTextHttp = false` (scheme control) — matches the harness's per-skill allowlist model
    - Microsoft-maintained, MIT, on NuGet.org (consumers can restore from default feeds)
  - **Rejected candidates:**
    - `idunno.Security.Ssrf` (blowdart) — technically excellent and actively maintained (v5.3.0 added TOCTOU hardening in May 2026), but distributed via **MyGet only**, which disqualifies it for a template whose consumers `nuget restore` from default feeds
    - `SsrfSharp` — does not exist on NuGet or GitHub (verified absent)
    - `Microsoft.Identity.Web` — no SSRF features
    - No .NET port of `ssrf-req-filter` / `ssrf_filter` exists
    - `IPNetwork2` alive but a CIDR primitive, not SSRF; only relevant if we had to build custom
  - **Open verification items at adoption time (flagged in §6 of `ssrf-defense.md`):**
    - NuGet download count for `Microsoft.Security.AntiSSRF` — survey couldn't query NuGet directly; verify popularity signal before merge
    - Confirm IPv6 ULA `fc00::/7` coverage in `IPAddressRanges.json`
    - Confirm `_editLock` lifecycle interacts correctly with `IHttpClientFactory`

- **Egress policy shape:**
  - `IEgressPolicy.AllowAsync(Uri target, IAgentIdentity identity)` — returns `EgressDecision { Allowed, Reason, MatchedAllowlistEntry }`.
  - Allowlist entries are declared in `SkillManifest`:
    ```yaml
    egress:
      allowlist:
        - host: "api.github.com"
          schemes: ["https"]
          ports: [443]
        - hostPattern: "*.azure-api.net"
          schemes: ["https"]
          ports: [443]
    ```
    Wildcards limited to leftmost label only; no full-regex (regex-allowlist is its own SSRF vector).
  - Decisions audit-logged with: timestamp, identity (tenant/user/agent), target URI, matched allowlist entry (or "none"), final IP after DNS resolution, decision.

- **Sandbox completion verification:**
  - Verify `ProcessSandboxExecutor` (Windows Job Objects) and `DockerSandboxExecutor` exist per CLAUDE.md. If they do, this PR adds the egress layer on top. If either is missing or stub-only, it gets completed in this PR — splitting would force a sandbox-without-egress intermediate state that lets a skill make unbounded network calls until the next PR.
  - HMAC attestation (per CLAUDE.md) preserved through the sandbox changes; existing attestation tests stay green.

- **Files:**
  - new `Domain.AI/Egress/` — `EgressDecision`, `EgressAllowlistEntry`, `IEgressPolicy`
  - new `Infrastructure.AI/Egress/` — `EgressPolicyHttpClientFactory`, SSRF filter (library-wrapped or custom per survey outcome), `JsonlEgressAuditWriter`
  - complete `Infrastructure.AI.Sandbox/` if executors are missing
  - update `SkillManifest` schema + parser to carry `EgressAllowlist`
  - new `documentation/security/ssrf-defense.md` — threat model + survey outcome + chosen library (or fallback rationale)

- **Depends on:** PR-1 (egress policy decisions are keyed by `IAgentIdentity`).

- **Risk:** **High** — sandbox correctness on Windows + Docker isolation modes (named pipe vs HTTP) + SSRF library choice all in one PR. Mitigation: SSRF survey is a discrete deliverable with a written decision rule (above); sandbox verification is the first step of the PR so its risk is bounded before egress work starts.

- **Acceptance:**
  - default-deny verified (HTTP call from a skill with no allowlist throws `EgressBlockedException`)
  - allowlisted call succeeds
  - SSRF attempt to private IP (10.x, 192.168.x) blocked even when allowlist matches by hostname (DNS rebinding test)
  - SSRF attempt to metadata IP (169.254.169.254) blocked
  - SSRF attempt via redirect to private IP blocked
  - non-HTTP scheme blocked
  - sandbox attestation (HMAC) preserved
  - `ProcessSandboxExecutor` + `DockerSandboxExecutor` both green on Windows + Linux runners
  - egress decision audit JSONL captures every decision
  - `documentation/security/ssrf-defense.md` published with the chosen library or fallback rationale

### PR-4: Graded Autonomy Refinement

- **Deliverable:** add `BlastRadius` enum + per-skill + per-environment overrides to `AutonomyTierPolicy`. State-changers default to Review even under Autonomous tier unless explicitly opted in.
- **Files:** `Domain.AI/Governance/AutonomyTierPolicy.cs`, `Application.Core/Permissions/AutonomyTierRuleProvider.cs`, config schema validator.
- **Depends on:** PR-2 (autonomy decision applied at the `ApprovalGate`).
- **Risk:** **Low** — extension of existing model.
- **Acceptance:** unit tests on tier × blast-radius matrix; existing autonomy tests stay green; new state-changer-defaults-to-Review test passes.

### PR-5: Incident-Response-Plan Abstraction

- **Deliverable:** `IncidentResponsePlan` config (incident type → skill set → autonomy override → required gates), loaded via `IOptionsMonitor<IncidentResponsePlanConfig>`; `IIncidentResponsePlanResolver` service.
- **Files:** new `Domain.Common/Config/AI/IncidentResponse/`; new `Application.AI.Common/Interfaces/IncidentResponse/`; new `Infrastructure.AI/IncidentResponse/`; integration with `ChangeProposalOrchestrator`.
- **Depends on:** PR-2, PR-4.
- **Risk:** **Low**.
- **Acceptance:** config validation; resolver picks the correct plan for an incident type; missing plan → falls back to defaults; hot reload works.

### PR-6: Magentic Orchestration Surface

- **Span schema research (executed 2026-06-05):** full schema + integration constraints → `documentation/architecture/magentic-spans.md`
- **Deliverable:** `IMagenticOrchestrator` skill wrapping MAF's Magentic; OTel spans per the schema doc; HITL plan-review hook routed through `EscalationTracker`.
- **Critical constraint from the span-schema research:** MAF's `MagenticOrchestrator` is **`internal`** — cannot instrument by inheritance. Instrumentation must subscribe to the public event stream:
  - `MagenticPlanCreatedEvent` (initial plan event)
  - `MagenticReplannedEvent` (replan event)
  - `MagenticProgressLedgerUpdatedEvent` (per-round progress event)
  - `MagenticPlanReviewRequest` / `MagenticPlanReviewResponse` (HITL pause point)
  - All derive from public `MagenticOrchestratorEvent`
- **Stall counter is NOT public** (lives on internal `MagenticTaskContext.TaskCounters`). The harness derives effective counters from the event stream — one progress-ledger event = one round; `MagenticReplannedEvent` after a stalled plan-review = one reset. Schema doc encodes this derivation explicitly.
- **API churn risk acknowledged:** MAF 1.0 GA shipped April 2026, but Magentic is still flagged experimental in samples (`#pragma warning disable MAAIW001`). Pin to the public event types only. Integration tests version-pin the event-type shape so a MAF bump surfaces as a test failure, not a runtime surprise.
- **Span tree (per schema doc):**
  - `invoke_workflow` (root)
    - `invoke_agent` (manager — Task Ledger as span events on this span)
      - `magentic.round` (one per ProgressLedgerUpdated — Progress Ledger as attributes)
        - `chat` (inherited from MAF/OTel GenAI)
        - `execute_tool` (inherited)
      - `magentic.plan_review` (HITL pause; lifetime = pause duration)
      - `magentic.reset` (one per stall→replan; child of manager span)
- **Span attributes:** `gen_ai.*` for spec-covered concepts (model, tokens, finish reasons), `gen_ai.orchestration.magentic.*` for harness-extension concepts (round number, stall count, replan reason, plan signoff state). Schema doc lists every attribute.
- **Content capture:** default off per OTel spec. When enabled: `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`, plus harness-extension `gen_ai.orchestration.magentic.plan` and `gen_ai.orchestration.magentic.replan_reason`.
- **Files:**
  - new `Infrastructure.AI/Orchestration/Magentic/` — event-stream subscriber, span emitter, HITL bridge
  - OTel registration in `Domain.AI/Telemetry/Conventions/` (extend `GenAiSemconvRegistry.cs`)
  - example consumer in `Presentation.ConsoleUI/Examples/`
  - integration with PR-2 — replan that proposes a state-changing action emits a `ChangeProposal`, not a direct effect
- **Depends on:** PR-2 (replan emits a `ChangeProposal`); PR-11 (content-capture policy / semconv-stability pinning).
- **Risk:** **Medium-High** — MAF Magentic still experimental; instrumentation depends on event-stream stability rather than orchestrator interface.
- **Acceptance:**
  - spans visible in Grafana/Tempo per the schema
  - all four event types produce the expected span / event
  - stall + replan exercised by a fixture skill, span shows derived stall count
  - plan-review path round-trips through `EscalationTracker`, span lifetime covers the pause
  - content-capture toggle respected (off by default, attributes absent)
  - MAF version-pin test asserts public event shape

### PR-7: A2A Surface

- **Deliverable:** expose MAF's A2A as a first-class harness surface from the outset. Every inter-skill call across the harness — in-process or cross-process — goes through the same A2A wire shape, so a consumer can split a skill into its own process later without changing call sites. Document the inter-agent message contract; ship the SRE↔Workspace example as the first concrete consumer. Identity propagation, OTel span linking, and authentication (mutual TLS or workload identity token, evaluated at design time) are part of this PR — not follow-ups.
- **Files:** new `Infrastructure.AI/A2A/`; documentation in `documentation/architecture/`; example in `Presentation.ConsoleUI/Examples/`; auth integration with PR-1's `IAgentIdentity`.
- **Depends on:** PR-1 (each A2A call carries the calling agent's identity).
- **Risk:** **Medium** — A2A standard is young (Linux Foundation since Jun 2025). Mitigation: thin wrapper over MAF + version-pinned protocol assertions in integration tests, so a MAF A2A bump surfaces as a test failure rather than a runtime surprise.
- **Acceptance:** in-process A2A call green; cross-process A2A call green (process A → process B with mutual auth); OTel spans capture caller + callee identities + correlation id; identity propagation validated end-to-end; protocol-version test asserts MAF A2A schema matches expected shape.

### PR-8: Workspace Skill Pack (Dagger-style)

- **Deliverable:** skill manifest declaring AllowedTools = `read_file`, `write_file`, `list_files`, `run_tests`, `run_lint`; DeniedTools = `shell_exec`, `raw_filesystem`; sandbox-required = true; egress allowlist = none. Tools resolve a working-copy path injected by sandbox.
- **Files:** new plugin folder under `plugins/workspace-skill/`; SKILL.md; tool implementations in `Infrastructure.AI/Tools/Workspace/`; keyed DI registration.
- **Depends on:** PR-2 (writes go through ChangeProposal), PR-3 (sandbox + egress).
- **Risk:** **Low** — bounded, additive.
- **Acceptance:** end-to-end demo: agent fixes a failing test, opens a ChangeProposal, gate runs tests + lint green, approval routes to user, merge applies the patch in the working copy.

### PR-9: GitOps Skill Pack (Flux + ArgoCD)

- **Deliverable:** skills `gitops-repo-audit`, `gitops-cluster-debug`, `gitops-knowledge` (RAG-backed) with **parity controllers** — every skill works against Flux and ArgoCD with controller-specific tool wrappers behind an `IGitOpsController` abstraction. All mutations are `ChangeProposal`s against a Git repo; **no direct cluster mutation** under any controller. DeniedTools includes `kubectl_apply`, `kubectl_delete`, `helm_upgrade`. K8sGPT MCP server consumed as the cluster-debug backend (not gracefully degraded — required, because "gracefully degraded" here is a corner that ships a half-working skill).
- **Files:** new plugin folder under `plugins/gitops-skill/`; SKILL.md; controller abstraction in `Application.AI.Common/Interfaces/GitOps/`; Flux + ArgoCD implementations in `Infrastructure.AI/GitOps/`; tool wrappers in `Infrastructure.AI/Tools/GitOps/`; MCP client config for K8sGPT.
- **Depends on:** PR-2, PR-3, PR-7 (A2A to Workspace skill for repo edits).
- **Risk:** **Medium-High** — two controllers double the surface but only doubles the test matrix; risk is in keeping the abstraction honest (no Flux-isms bleeding through the interface).
- **Acceptance:** repo-audit detects drift on both controllers; cluster-debug returns RCA without mutating on both controllers; remediation surfaces as ChangeProposal only; controller-abstraction test forbids controller-specific types from leaking into `Application` layer.

### PR-10: IaC Skill Pack (Terraform + Bicep, parity)

- **Deliverable:** Terraform and Bicep generation both ship in this PR. Each goes through its own gate chain: TF → `terraform validate` → `terraform plan` → Checkov + tfsec; Bicep → `bicep build` → ARM-TTK + Checkov. Both gates are `IChangeProposalGate` implementations under the unified PR-2 pipeline, so the consumer doesn't experience two different surfaces. Skill never deploys; deployment is a separate `ChangeProposal` flagged `BlastRadius=Critical`, requires Autonomous tier override AND human approval (no auto-merge). Generation, plan, and scan all run inside PR-3's sandbox with egress allowlisted to the relevant registries (HashiCorp Registry / Microsoft container registry for `bicep` tooling).
- **Files:** new plugin folder under `plugins/iac-skill/`; SKILL.md; backend abstraction `IIacGenerator` in `Application.AI.Common/Interfaces/Iac/`; Terraform + Bicep implementations in `Infrastructure.AI/Iac/`; gate implementations in `Infrastructure.AI/Changes/Gates/Iac/`; Checkov + tfsec + ARM-TTK runners.
- **Depends on:** PR-2, PR-3, PR-4 (BlastRadius), PR-9 (uses GitOps for repo writes).
- **Risk:** **High** — five external CLI dependencies (terraform, bicep, checkov, tfsec, arm-ttk) across two OS targets. Mitigation: each is pinned to a verified version and runs inside the sandbox container image with versions baked in.
- **Acceptance:** generated TF passes Checkov + tfsec on a real Azure module; generated Bicep passes ARM-TTK + Checkov on a real Azure module; deploy proposal blocked without explicit approval on both backends; gate chain identical-shaped from consumer's perspective (test asserts the `IChangeProposalGate` interface is the only surface).

### PR-11: OTel GenAI Content-Capture Policy

- **Deliverable:** pin `OTEL_SEMCONV_STABILITY_OPT_IN` value; add `AppConfig.Telemetry.ContentCapture` (off by default); PII redaction filter for prompt/tool content when enabled; Magentic ledger spans land here; updates `documentation/blueprints/agentic-harness-observability.md`.
- **Files:** `Domain.AI/Telemetry/Conventions/GenAiSemconvRegistry.cs`; new `Infrastructure.AI/Telemetry/Redaction/`; config schema.
- **Depends on:** PR-6 (Magentic spans).
- **Risk:** **Low**.
- **Acceptance:** redaction filter unit tests; content-capture off-by-default verified; semconv-stability env var documented.

### PR-12: OWASP Agentic Top 10 Map + Eval Pack

- **Fixture design (executed 2026-06-05):** full 10-row fixture spec, scoring mechanisms, runtime budget, CI integration → `documentation/security/owasp-agentic-top-10-evals.md`
- **Official row IDs confirmed:** `ASI01` (Agent Goal Hijack), `ASI02` (Tool Misuse), `ASI03` (Identity & Privilege Abuse), `ASI04` (Agentic Supply Chain), `ASI05` (Unexpected Code Execution), `ASI06` (Memory/Context Poisoning), `ASI07` (Insecure Inter-Agent Communication), `ASI08` (Cascading Failures), `ASI09` (Human-Agent Trust Exploitation), `ASI10` (Rogue Agents). Verified against OWASP GenAI Security Project (2025-12-09 publication, 2026 edition). Findings doc list matches official 1:1.
- **Deliverable:** `documentation/security/owasp-agentic-top-10.md` mapping each row to a named harness control with a file path (separate from the eval spec above); eval pack exercises **all 10 rows**. **CI gate is blocking from day one.** Informational gates train consumers to ignore security results.
- **Mechanical-only scoring (per Quality Bar):** every fixture scores via string match, regex, `AgentInvocationResult.ToolsInvoked` tool-call assertion, JSONL audit-log inspection, `Result<T>` code check, or sandbox audit entry. **No LLM-as-judge in the CI gate.** LLM is the system-under-test; scoring is deterministic.
- **Per-row → PR mapping (from fixture spec):**
  - `ASI01` Goal Hijack → PR-2 (`ChangeProposal` gate prevents non-approved actions)
  - `ASI02` Tool Misuse → PR-2 (DeniedTools + gate)
  - `ASI03` Identity & Privilege Abuse → PR-1 (`IAgentIdentity` + RBAC)
  - `ASI04` Agentic Supply Chain → PR-9/10 (skill-manifest validation, signed plugins)
  - `ASI05` Unexpected Code Execution → PR-3 (sandbox + egress)
  - `ASI06` Memory/Context Poisoning → PR-2 + Task #6 (gate validates memory writes, tenant isolation)
  - `ASI07` Insecure Inter-Agent Comm → PR-7 (A2A with identity propagation + mutual auth)
  - `ASI08` Cascading Failures → PR-6 (Magentic stall-replan + reset)
  - `ASI09` Human-Agent Trust Exploitation → PR-2 (ApprovalGate cannot be skipped)
  - `ASI10` Rogue Agents → PR-1 + PR-9/10 (identity revocation + manifest signing)
- **Runtime budget (verified by fixture spec):** ~66s p95 sequential, ~30s parallel. Well under 5-minute ceiling. Largest single fixture is `asi08` at ~25s (three orchestrator turns).
- **Out-of-scope, routed to offline eval pack (not CI):**
  - Multi-turn social engineering for `ASI09` — too session-stateful for deterministic CI
  - Cross-session context-window exhaustion for `ASI06` — same reason
  - Both move to an offline eval pack documented in `owasp-agentic-top-10-evals.md` §Out-of-scope
- **Files:**
  - new `documentation/security/owasp-agentic-top-10.md` — control map (one row per OWASP entry → harness control file path)
  - new `src/Content/Tests/Owasp.AgenticTopTen.Evals/` (or wherever the existing eval framework lives — fixture spec confirms `EvalCase` / `MetricSpec` / `IEvalMetric` / `AgentInvocationResult` from the Phase 5 framework)
  - CI workflow integration in `.github/workflows/` — `dotnet test --filter Category=OwaspAgentic`, blocking
- **Depends on:** PR-1 (`ASI03`), PR-2 (`ASI01` / `ASI02` / `ASI09`), PR-3 (`ASI05` / `ASI10`), PR-6 (`ASI08`), PR-7 (`ASI07`), PR-9 + PR-10 (`ASI04` / `ASI10`).
- **Risk:** **Medium** — blocking gate means a flaky eval fails CI. Mitigation: every fixture's scoring is mechanical per the spec; no LLM nondeterminism in pass/fail logic. Bypass mechanism: labelled PR + recorded justification (per fixture spec) — disincentivises silent skips but allows verified false-positives.
- **Acceptance:**
  - every row has a named control + a deterministic eval per the fixture spec
  - all 10 fixtures pass against the post-PR-11 harness
  - CI blocks merge on failure (no informational mode)
  - eval pack runs in <90 seconds sequential, <60 seconds parallel
  - bypass requires labelled PR + audit entry

---

## 6. Cross-Cutting Concerns

- **Backward compatibility:** every runtime-path PR ships behind a flag. Default off until consumer opts in. CLAUDE.md "Replace, don't deprecate" applies *within* a PR (no compat shims); across PRs the flag IS the boundary.
- **Tests:** 80% coverage minimum per existing rule. Unit + integration for each PR. Eval-pack tests for PR-12.
- **Docs:** every PR updates the relevant section of `documentation/onboarding/` chapter or `documentation/architecture/` page. Onboarding chapter 14 (Skill Training) is the precedent.
- **Memory writes:** after each PR, update `MEMORY.md` index and write a project memory file capturing what shipped + why + non-obvious decisions.
- **Code review:** every PR runs `/code-review` then `/review-changes deep` per `.claude/rules/review-cadence.md`. PR-1 + PR-2 + PR-3 + PR-6 also warrant `security-reviewer` agent (PR-1 added because it extends the tenant ambient; security regression risk).
- **Multi-Claude coordination:** while two Claude sessions work the codebase, both read `.claude/in-flight.md` at session start and update it on claim / release. PR-1 must not start until Session B's Task #6 PR-3 (tenant isolation final layer) merges and Session A confirms the merged ambient signature. PR-2 and PR-3 design spikes can run during the wait (no runtime-path edits).
- **Audit store reuse:** all new audit writes (gate decisions, egress decisions, autonomy decisions, OWASP eval results) go through the existing JSONL audit store from the Escalation subsystem. No new audit infrastructure.

---

## 7. Explicit Non-Goals (this plan does NOT do)

- **No new model providers.** Anthropic / Azure OpenAI / Foundry are enough.
- **No competitor parity matrix.** This is a template; it doesn't need to clone Azure SRE Agent, Bedrock AgentCore, or Gemini Cloud Assist. It needs *the patterns* they converged on.
- **No A2A protocol invention.** Thin MAF wrapper only.
- **No deployment orchestration.** PR-10 stops at proposal + scan + plan. Actual `terraform apply` / `az deployment` is a future flag-gated PR; out of scope here.
- **No GitHub Actions / GitLab pipeline rewrite.** Skill packs may *call* CI; they do not *become* CI.
- **No quantitative claims taken from section 9 flagged facts** (coordination square-law, 53–86% duplication, etc.). Plan does not optimize against unverified numbers.

---

## 8. Open Questions for User (decide before kicking off PR-1)

These are genuine architectural choice points where the plan picks a direction but the user owns the call. Per the global "Optimize for Best Outcome, Not Speed" rule, every recommendation below is for the best end-state; no question is framed as "which is cheaper".

1. **Phase ordering — Foundations-first (PR-1/2/3) or pilot-Skill-Pack-first (PR-8 Workspace)?** Recommend Foundations-first. PR-2 (`ChangeProposal`) is load-bearing for every skill pack; building a skill pack before its load-bearing abstraction means either (a) the skill is built against a temporary shape that the abstraction then replaces — i.e. throwaway code — or (b) the abstraction is reverse-engineered from a single skill's needs, which produces a worse abstraction. Neither is acceptable.
2. **Identity provider depth in PR-1 — `IAgentIdentity` abstraction + dev provider only, or full Entra Agent ID wiring in the same PR?** Recommend **full Entra Agent ID wiring in PR-1**. The credential hierarchy (federated → managed identity → cert → secret) is part of the abstraction's correctness; shipping only the dev provider means the production providers get retrofitted later against an abstraction that didn't have to consider their quirks (token refresh, scope, audience). Done right means done together. (My prior recommendation to defer was speed-coded; killed.)
3. **A2A — is PR-7 in the critical path or follow-up?** Recommend **in the critical path, before PR-8**. Every inter-skill call goes over A2A from day one, so a skill that lives in-process today can move to its own process without changing call sites. Building in-process-only first means re-plumbing every consumer when A2A lands, which violates "Replace, don't deprecate". (My prior recommendation to defer was speed-coded; killed.)
4. **IaC scope (PR-10) — Terraform + Bicep parity, or pick one?** Recommend **parity**. The harness is multi-cloud-capable; shipping Bicep-only Azure-locks the template, and consumers needing AWS / GCP via Terraform write the second backend themselves — which is the corner-cutting the Quality Bar explicitly forbids. The cost is double the gate plumbing; the value is a template that doesn't force a cloud choice on the consumer. (My prior Bicep-first recommendation was speed-coded; killed.)
5. **GitOps target (PR-9) — Flux only, ArgoCD only, or parity?** Recommend **parity**. Flux + ArgoCD cover the bulk of the enterprise install base; an `IGitOpsController` abstraction with both implementations is the same shape as the IaC parity in PR-10. Single-controller skill packs leave half the consumers writing the other half. (My prior Flux-only recommendation was speed-coded; killed.)
6. **OWASP eval pack (PR-12) — blocking or informational?** Recommend **blocking from day one**. Informational gates teach consumers that security failures are advisory; in a security-teaching template that is the wrong default. Eval determinism is the mitigation, not gate relaxation. (My prior "informational first cut" recommendation was speed-coded; killed.)

Two newly-surfaced questions that the revised scope creates:

7. **SSRF defense library for PR-3 — RESOLVED 2026-06-05.** Survey executed; verdict: **ADOPT `Microsoft.Security.AntiSSRF`** (Microsoft-maintained, MIT, on NuGet.org, post-resolve `ConnectCallback` filtering = real DNS-rebinding defense). Full report: `documentation/security/ssrf-defense.md`. Three verification items remain at adoption time (NuGet download signal, IPv6 ULA coverage check, `_editLock` lifecycle with `IHttpClientFactory`) — flagged in §6 of that doc.
8. **`IGitOpsController` and `IIacGenerator` — are these abstractions accepted as load-bearing harness types, or kept skill-private?** Recommend load-bearing (in `Application.AI.Common/Interfaces/`). Skill-private hides the abstraction from consumers who want to add a third controller / backend, which contradicts the template's purpose.

---

## 9. Sequencing Summary

```
PREREQUISITE                  : Task #6 PR-3 multi-tenant isolation — ✅ MERGED 2026-06-05
Phase A (Foundations)         : PR-1 -> PR-2 -> PR-3        (sequential; each load-bearing; PR-1 unblocked)
Phase B (Autonomy & Orchestr) : PR-4 || PR-5 -> PR-6        (PR-4/PR-5 parallel, PR-6 after)
Phase C (Skill Packs)         : PR-7 -> PR-8 -> PR-9 -> PR-10  (sequential; A2A first per Q3)
Phase D (Cross-Cutting)       : PR-11 || PR-12              (parallel, end of plan)
```

Total: 12 PRs. No effort estimate stated as a goal — duration is whatever the right work takes. Estimates may be produced per-PR at design time as facts (for the user's planning), not as selection criteria.

**Pre-PR-1 design work completed 2026-06-05:**
- ✅ PR-2 design spike — deepened in §5 PR-2 above
- ✅ PR-3 SSRF survey — verdict ADOPT `Microsoft.Security.AntiSSRF`; report at `documentation/security/ssrf-defense.md`
- ✅ PR-6 Magentic span specification — full schema at `documentation/architecture/magentic-spans.md`; critical finding: orchestrator is internal, instrument via public event stream
- ✅ PR-12 OWASP eval-pack fixture design — 10 mechanical-scoring fixtures, ~66s p95 sequential, at `documentation/security/owasp-agentic-top-10-evals.md`

**Status:** PR-1 ready to start. Single Claude session going forward (multi-session coordination apparatus retired 2026-06-05).

---

## 10. Acceptance Criteria for "This Plan Done"

- Every PR has shipped and is on `main`.
- A single end-to-end demo run: a real GitHub issue → Workspace skill proposes a fix → ChangeProposal runs SelfValidationGate (tests + lint green) → ApprovalGate routes to user → user approves → MergeGate applies → audit JSONL captures every gate decision with the agent's `IAgentIdentity` and BlastRadius.
- OTel traces show the full `invoke_agent → chat → execute_tool` tree per GenAI semconv, including Magentic ledger spans when Magentic is the orchestrator.
- OWASP Agentic Top 10 doc is published; every row links to the harness control file path.
- Memory index has entries for each shipped PR.

---

*End of plan. No code written.*

*Section 8 questions all answered 2026-06-05 (recommended path on every question). PR-1 blocked on Session B's Task #6 PR-3 merge. Session A can execute the wait-state work items in §9 without collision.*
