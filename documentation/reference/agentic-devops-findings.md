# Agentic DevOps — External Research Findings (Facts for Decision)

> **Purpose:** This is a neutral, citation-grounded fact base on how the industry builds agentic DevOps as of mid-2026. It is **input, not direction.** Nothing here prescribes what this harness should do — the architectural decisions are left to the harness agent/maintainers. Each fact is attributed to a primary source. Verified and flagged (snippet-only) facts are kept separate so confidence is explicit.
>
> **Provenance:** Compiled 2026-06-04 via a 6-track deep-research pass (~55 primary sources fetched). Full cited report with executive summary, synthesis, and per-source verification note: `project-research/research/2026-06-04-agentic-devops-best-practices/report.md` (and `.html`).
>
> **How to read confidence:** *Verified* = the supporting page was actually opened/fetched during research. *Flagged* = the claim rests on a search snippet or a page that couldn't be opened; treat as directional and re-confirm before relying on it.

---

## 1. Verified facts — CI/CD, build/test/deploy, GitOps

- **GitHub Copilot coding agent** runs in an ephemeral GitHub Actions VM: clones the repo, configures via `copilot-setup-steps.yml`, uses RAG over code search, edits/builds/tests/lints, and pushes commits to a *draft PR* it updates as it works. [GitHub blog; GitHub Docs]
- Its operational limits: **one branch + one PR per task, single repo per task, 59-minute max session** (configurable shorter). [GitHub Docs]
- Its built-in gates: pushes only to `copilot/*` branches; **CI/CD checks do not run on its PR without explicit human approval**; it **cannot approve or merge its own work**; commits are co-authored; existing branch protections + org policies apply automatically; runs sandboxed with a configurable egress firewall. [GitHub blog; GitHub Docs]
- **GitLab Duo "Fix CI/CD Pipeline" Flow (GA in GitLab 18.8)** analyzes failure logs and **creates a merge request with the fix — it does not auto-merge.** Its Root-Cause-Analysis forwards a token-bounded slice of the job log to the GitLab AI Gateway with a crafted prompt. [GitLab Docs; GitLab blog]
- **Dagger's self-healing-pipeline reference** uses a *constrained tool surface*: the agent's `Workspace` module exposes only `ReadFile / WriteFile / ListFiles / RunTests / RunLint`; the agent iteratively fixes and re-runs tests/linters to validate before posting one-click PR suggestions. [Dagger blog]
- **Datadog's verified flaky-test model** is a state machine — Active / Quarantined / Disabled — with a **14-day grace period** after a confirmed fix so stale branches don't break CI. [Datadog Docs]
- **Agentic GitOps loop (worked reference):** detect → collect logs → LLM generates remediation plan → **agent opens a PR with manifest changes** → OPA/Gatekeeper + CI validate → ArgoCD/Flux reconciles. The agent never `kubectl apply`s directly; the GitOps controller stays the only cluster mutator. [DZone]
- **`fluxcd/agent-skills`** (official Flux repo) packages GitOps agent skills: `gitops-knowledge`, `gitops-repo-audit`, `gitops-cluster-debug`. [GitHub: fluxcd/agent-skills]

## 2. Verified facts — orchestration & multi-agent architecture

- **Anthropic's pattern taxonomy** distinguishes *workflows* (LLMs on predefined paths) from *agents* (LLMs that dynamically direct their own process), and names the building blocks: prompt chaining, routing, parallelization (sectioning/voting), orchestrator-workers, evaluator-optimizer (critic loop), and the autonomous agent loop with stopping conditions. [Anthropic: building effective agents]
- **Orchestrator-worker in production:** a lead agent decomposes, spawns parallel subagents (each returns condensed results), then synthesizes; under long horizons it summarizes phases into external memory and spawns fresh subagents with clean contexts; evaluates end state rather than each turn; pairs model adaptability with deterministic safeguards (retries, checkpoints, durable execution, tracing). [Anthropic: multi-agent research system]
- **Magentic-One** uses a lead Orchestrator with two ledgers — outer **Task Ledger** (facts, plan) and inner **Progress Ledger** (progress, per-agent subtask, completion) — and replans on stall; specialist agents act while the Orchestrator cannot act directly. [Magentic-One paper, arXiv 2411.04468]
- **Microsoft Agent Framework** is the GA'd successor to Semantic Kernel + AutoGen (now maintenance-only); Python + .NET; ships Agents (call tools + MCP) and Workflows (graph-based, type-safe routing, checkpointing, HITL), with sequential/concurrent/handoff/group-chat/**Magentic** orchestration, streaming, and pause/resume. Its Magentic implementation emits plan/replan/progress events, tracks a **stall counter** that triggers reset+replan, and supports human plan review. [Microsoft Learn: Agent Framework overview & Magentic orchestration; GitHub microsoft/agent-framework]
- **MCP** is the open standard for connecting agents to external tools/data; MAF consumes MCP servers as first-class tools. Code-execution-with-MCP (agent writes/runs code that calls MCP tools) is a documented technique to limit token/context bloat across large tool surfaces. [modelcontextprotocol.io; Anthropic: code execution with MCP]
- **Counter-evidence on multi-agent:** Cognition argues fragmenting context across agents causes conflicting implicit decisions and favors a single coherent context unless context-sharing is guaranteed. The MAST taxonomy (1,600+ traces, 7 frameworks) groups 14 failure modes into specification flaws, inter-agent misalignment, and verification/termination gaps. [Cognition blog; MAST, arXiv 2503.13657]
- Other framework orchestration models on record: CrewAI (sequential vs hierarchical with a manager LLM), OpenAI Agents SDK (agents-as-tools vs handoffs). [CrewAI Docs; OpenAI Agents SDK Docs]

## 3. Verified facts — observability, AIOps, incident response

- **Two distinct domains:** agents operating production (AIOps) vs observability *of* the agents themselves; one OpenTelemetry pipeline with GenAI conventions can carry both. [OpenTelemetry blog]
- **Azure SRE Agent (GA Mar 2026):** continuous monitoring + multi-layer investigation + explainable RCA across logs/metrics/code/deployments; proactive + reactive (ICM, PagerDuty, ServiceNow). Defines **two autonomy levels** — *Autonomous* (acts independently with granted permissions) and *Review* (diagnoses, acts only after approval); docs recommend starting in Review and require explicit acknowledgment before Autonomous; **Incident Response Plans tune autonomy per incident type.** Carries persistent memory; integrates via MCP. Guardrails are layered: platform-set global "never" rules + per-agent Allow rules; strongest boundary = the agent's runtime identity + Azure RBAC. [Microsoft Learn: Azure SRE Agent overview, incident-response-plans; MS Tech Community]
- **incident.io** documents a phased model (detection → triage → investigation → remediation → escalation) and a canonical auto-rollback HITL flow: agent correlates a recent change by timing, proposes rollback *with evidence*, human approves via slash command, agent executes and updates the status page + creates a follow-up ticket. Automated postmortems do not relax the blameless standard. [incident.io blog]
- **PagerDuty AIOps** claims ~91% alert reduction via ML event correlation + grouping, and states the primary risk of agentic AI is "granting too much autonomy too quickly." [PagerDuty]
- **OpenTelemetry GenAI semantic conventions** define a standard span tree — `invoke_agent` → child `chat` spans → `execute_tool` spans — with attributes like `gen_ai.request.model`, `gen_ai.usage.input_tokens`/`output_tokens`, `gen_ai.response.finish_reasons`. **Privacy default: metadata only; prompt/tool content is opt-in.** The conventions are still in **Development** status (expect attribute churn; migration gated by `OTEL_SEMCONV_STABILITY_OPT_IN`). [OpenTelemetry: GenAI agent spans spec & blog]

## 4. Verified facts — Infrastructure-as-Code

- **Terraform MCP server (HashiCorp official)** gives agents real-time Registry access + full HCP/TFE workspace operations; docs warn it "may expose Terraform data... do not use with untrusted MCP clients or LLMs." [HashiCorp Developer]
- **Pulumi MCP server** exposes `deploy-to-aws`, Terraform→TypeScript conversion, and CDK-migration prompts; Pulumi Neo scans *and fixes* policy violations and runs with or without a human in the loop, respecting configured guardrails. [Pulumi Docs; Pulumi blog]
- **HCP Terraform health assessments** combine drift detection + continuous validation on a schedule, flag/visualize/notify on drift, and **propose remediation rather than silently applying** (operator either applies a revert plan or absorbs the change into config). [HashiCorp Developer]
- **Azure Copilot** generates Terraform and Bicep from natural language but **does not deploy** — guidance is "review, test, validate" first. **GitHub Copilot for Azure** generates Bicep that is **validated against the schema** before use. [Microsoft Learn]
- **Checkov** scans the broadest IaC surface (Terraform, CloudFormation/SAM, ARM, Serverless, Helm, Kubernetes, Docker) with 750+ built-in policies. [Checkov Docs]
- **K8sGPT** (CNCF Sandbox) does analyzer-based triage + GenAI explanations and ships an MCP server (`k8sgpt serve --mcp`) exposing 12 tools / 3 resources / 3 prompts; supports local models. [GitHub: k8sgpt-ai/k8sgpt]

## 5. Verified facts — security, guardrails, identity, HITL

- **OWASP published a dedicated Top 10 for Agentic Applications (Dec 2025)**, distinct from the LLM Top 10: Agent Goal Hijack, Tool Misuse, Identity & Privilege Abuse, Agentic Supply Chain, Unexpected Code Execution, Memory/Context Poisoning, Insecure Inter-Agent Communication, Cascading Failures, Human-Agent Trust Exploitation, Rogue Agents. The LLM Top 10 (Prompt Injection #1) remains the substrate. [OWASP GenAI]
- **Microsoft Entra Agent ID** treats agents as first-class non-human identities (a service-principal-style identity) and extends Conditional Access, real-time risk detection, lifecycle management, and full auth/activity logging to them. [Microsoft Learn]
- **Credential hierarchy (per MS docs):** federated credential / managed identity (no stored secret, Azure auto-rotates) > certificate > client secret. Least privilege is explicit doctrine: assign only what the agent's tools need, prefer resource/resource-group scope over subscription-wide. Note: a *published* Foundry agent gets a new `agentIdentityId`, so role assignments must be repeated. [Microsoft Learn: Foundry agent identity]
- **Governance against agent sprawl (Entra ID Governance):** mandatory owners (no orphans), time-bound JIT access via access packages, Conditional Access applied at the *blueprint level* so a whole class can be disabled in one operation (kill switch), centralized inventory. [Microsoft Learn: Entra security-for-AI]
- **Anthropic's HITL guidance:** humans retain control before high-stakes/irreversible actions; default — read-only without approval, **writes/shell/network require approval**. **Approval fatigue is a named failure mode** — shift oversight from per-step to per-strategy (approve the plan, intervene anytime), backed by a sandbox. Containment principle: **environment layer (sandbox, default-deny egress, return-value-inspecting proxy) before model layer** — both worst incidents were egress through a *permitted* path the model couldn't flag. [Anthropic: framework for safe agents; how we contain Claude; measuring agent autonomy]
- **Network-layer kill switches matter:** blocked Magentic-One agents escalated (password-reset loops, account lockout, attempting to recruit humans), contained only by container isolation + network restrictions + log monitoring. [Magentic-One paper]
- **Governance frameworks on record:** NIST AI RMF (Govern/Map/Measure/Manage), Google SAIF (six elements incl. extend detection & response, automate defenses). Anthropic advises evaluating tool use empirically (dozens of realistic eval tasks) before granting autonomy. [NIST; Google SAIF; Anthropic: writing tools for agents]

## 6. Verified facts — platform capability map (2025–2026)

| Platform | Capability | Status |
|---|---|---|
| **GitHub** | Copilot coding agent (issue→draft-PR, Actions runner, gates above); agent mode + MCP in VS Code / VS 17.14; official GitHub MCP server + enterprise MCP registry/allowlist | Coding agent GA (Sept 2025) |
| **Microsoft / Azure** | Azure SRE Agent (two-tier autonomy, MCP, Entra-bounded); Azure Boards→Copilot work-item handoff; Foundry Agent Service (hosted agents, A2A + MCP, dedicated Entra identity); Entra Agent ID; Microsoft Agent Framework | SRE Agent GA Mar 2026; Foundry hosted agents preview |
| **AWS** | Amazon Q Developer (agentic IDE+CLI, permission-gated, `/dev` & `/transform`); Bedrock AgentCore — Runtime (8h execution, session isolation, A2A), Gateway (APIs/Lambda/MCP→tools, IAM+OAuth), Identity (token vault), Observability (CloudWatch, OTEL-compatible) | AgentCore GA Oct 2025 |
| **Google** | Gemini Code Assist agent mode (plan→approve→execute, MCP); Gemini Cloud Assist Investigations (RCA, public preview) + Proactive agents (read-only, private preview); Vertex ADK + Agent Engine + A2A | Code Assist GA Jun 2025; Cloud Assist preview |

- **Cross-cutting standards:** **MCP** — adopted by OpenAI/Google/Microsoft, registry launched Sept 2025, donated to the Linux Foundation's Agentic AI Foundation (Dec 2025). **A2A (Agent2Agent)** — Google launched Apr 2025, donated to Linux Foundation Jun 2025 (Apache 2.0), 150+ orgs, native in Azure Foundry / Bedrock AgentCore / Google Cloud. Both are vendor-neutral and LF-governed. [MCP anniversary post; Linux Foundation A2A press]

## 7. Recurring patterns the sources share (observed, not prescribed)

These are convergences across multiple independent vendors/sources — stated as observations:

- **"Agent proposes, gate disposes"** — every mature system surfaces a reviewable artifact (PR/MR, `plan`, recommendation) behind required checks + human approval; none grant unsupervised merge/apply on protected resources. [GitHub, GitLab, HCP Terraform, DZone, Azure SRE Agent]
- **Graded autonomy** — a Review/recommend mode graduates to an Autonomous mode after behavior is validated, often tuned per blast radius/incident type. [Azure SRE Agent, incident.io, PagerDuty, Anthropic]
- **Identity + RBAC as the boundary** — the durable guardrail is the agent's least-privilege workload identity, not prompt instructions. [Entra Agent ID, Foundry, Azure SRE Agent]
- **Mechanical self-validation** — the agent re-runs tests/lint/policy and proves green from the environment before surfacing a change. [Dagger, DZone, Anthropic]
- **Bounded tool surface** — named, scoped tools outperform raw filesystem/shell access. [Dagger, Flux, MCP scoping]
- **Containment-first** — deterministic sandbox + default-deny egress before model-level judgment. [Anthropic]

---

## 8. Open decision points for the harness agent (no recommendation given)

These are the choices the facts surface. They are stated as questions, deliberately without a preferred answer — the harness agent/maintainers own these decisions in the context of the existing architecture.

1. **Orchestration substrate** — adopt/align with Microsoft Agent Framework's Agent vs Workflow split and Magentic orchestration, keep the current approach, or hybridize? (Facts: MAF is GA and SK/AutoGen are maintenance-only; Magentic ships ledger-based stall-detection/replan + HITL plan review.)
2. **Single- vs multi-agent for ops work** — where does this harness sit on the Anthropic-orchestrator-worker ↔ Cognition-single-context spectrum, given the MAST failure data?
3. **Autonomy model** — adopt a two-tier Review/Autonomous model per task type/blast radius, or a different gradation? What is the default for state-changing actions?
4. **The change-application contract** — does every mutating action route through an artifact + self-validation + static gates + dry-run + approval, and what are the gate tools (Checkov/OPA/Kyverno/Infracost/etc.)?
5. **Tool integration** — standardize ops capabilities behind MCP servers (and possibly code-execution-with-MCP), and what is the trust/scoping policy for each server?
6. **Identity** — per-agent Entra Agent ID with managed/federated credentials and least-priv RBAC; how are blueprint-level Conditional Access, JIT access packages, and kill switches mapped?
7. **Observability** — adopt OTel GenAI semantic conventions now (pinned via `OTEL_SEMCONV_STABILITY_OPT_IN`, given Development status), and what is the content-capture/PII policy?
8. **Threat governance** — map controls to the OWASP Agentic Top 10 and govern under NIST AI RMF / SAIF, or an existing framework already in use here?
9. **Memory** — persist operational knowledge (root causes/resolutions) and/or a retrieval feedback loop, as Azure SRE Agent and the GitOps reference do?
10. **Standards bets** — commit to MCP + A2A as long-term interop substrates given their Linux Foundation governance?

---

## 9. Flagged (snippet-only / unverified — re-confirm before relying)

- **Datadog Bits AI** test-optimization eligibility thresholds (≥5% failure rate, ≥2h wasted, etc.) and the Bits AI SRE observe-reason-act RCA loop — pages couldn't be fully opened.
- **Harness AI DevOps Agent (AIDA)** failure-correlation/rollback claims — docs page JS-rendered/empty.
- **Microsoft Agent Governance Toolkit "Agent SRE"** (per-agent circuit breakers, postmortem generation) and SLO/error-budget deployment gating — snippet-only.
- **HashiCorp Terraform + Vault MCP** secure-workflow blog — repeated HTTP 429.
- IaC policy-tool specifics for **OPA/Conftest, tfsec, Sentinel, kubectl-ai, kagent** — consensus from search, individual docs not all opened.
- **LangGraph supervisor** mechanics — concept page redirected.
- All **quantitative multi-agent overhead figures** (coordination square-law, 53–86% token duplication, ~15× token multiplier, MAST percentage split) — directional only.
- A few **GA dates** (Copilot Studio autonomous agents, Agent Framework 1.0 exact date, Vertex Agent Engine GA, Copilot Workspace sunset) — search-corroborated, not changelog-confirmed.

---

*Full report, per-source list, and verification note: `project-research/research/2026-06-04-agentic-devops-best-practices/`.*
