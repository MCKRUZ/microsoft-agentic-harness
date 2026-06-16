# Handoff prompt — delivery-layer CI/CD + DevOps setup for the harness

> Paste the block below into a fresh Claude Code session opened **in this repo**
> (`microsoft-agentic-harness`). It is self-contained. Generated 2026-06-15 after a
> cross-repo clarification session.

```
We're setting up the delivery-layer CI/CD + DevOps governance for THIS project
(microsoft-agentic-harness), following the "Intent Driven Development" delivery
standard. Fix THIS project's gaps first; harvesting proven pieces up into the
methodology's reusable kit comes later. Do NOT build anything until you've read
the sources and proposed a plan I approve.

THE THREE REPOS (mental model):
- delivery-standard (C:\Users\kruz7\OneDrive\Documents\Code Repos\MCKRUZ\delivery-standard)
  = the METHODOLOGY (the "how", like Agile). docs/the-rails.md defines the gate model;
  GOLD-STANDARD.md section 10 defines "the kit" — the reusable template that lives THERE,
  not here. READ docs/the-rails.md first; it is the spec for the gates.
- THIS repo (microsoft-agentic-harness) = an example project built the methodology's way,
  and the first place we install the rails.
- "devops-pipeline" is abandoned; project-research's golden-build.html is background
  research only and carries unverified flags — don't paste its config blind.

TOOLCHAIN: Claude Code (CLAUDE.md is the PRIMARY instruction file here; .claude/ holds
hooks/agents/skills). NOT GitHub Copilot. Stack: .NET 10 / Azure.

ALREADY EXISTS — DO NOT REBUILD (verified by inventory):
- .github/workflows/ci.yml — build + test + coverage + OWASP Agentic hard gate.
  Job/check names: "build-and-test" and "OWASP Agentic Top-10 Gate".
- eval-suite.yml + eval-datasets/ (8 golden sets + owasp-agentic-top-10).
- Observability: Dashboards/ (Grafana/Prometheus/Tempo/Postgres) + scripts/otel-collector.
- agents/ (orchestrator, research, harness-proposer, default), skills/, plugins/.
- In-app runtime governance (autonomy tiers, sandbox, escalation, A2A/JWT identity) —
  production-grade already; that's the INNER layer, not the delivery layer we're fixing.

GAPS TO FIX (delivery layer, "matters now", prioritized):
1. Branch protection + CODEOWNERS — nothing enforces "a non-author human reviews before
   merge to main." Required checks = "build-and-test" + "OWASP Agentic Top-10 Gate".
   Deliver branch protection as code (ruleset JSON) + a thin gh-api apply script.
2. Stop hook (.claude/hooks/) — block an agent from finishing while build/tests are red
   (the methodology's "stop-gate").
3. .claude/settings.json — permission policy: auto-allow safe ops, ASK on gated paths
   (.github/, auth, migrations, future infra).
4. Grader — a Claude workflow + agent that reviews a PR diff against its spec and posts a
   verdict as a PR comment (ADVISES, never blocks; "it ran" is the required check).
5. Security-reviewer agent gate — runs on gated paths; blocks on HIGH.

DEFERRED — DO NOT BUILD (no cloud deployment exists): deploy pipeline/rollback, Bicep/IaC,
Key Vault prod secrets, specs/ system. Revisit only if/when the project gains cloud infra.

LOW-PRIORITY NOTE: AGENTS.md is currently GitNexus boilerplate only. Under Claude Code,
CLAUDE.md is primary, so this is minor — only give AGENTS.md real content (above the
GitNexus markers) if multi-tool/Copilot support becomes a goal.

RULES: Verify any config against live vendor docs before pasting. Work on a branch off main;
do NOT commit/push or apply live branch protection via gh without my explicit OK.

FIRST STEP: read delivery-standard/docs/the-rails.md, this repo's .github/workflows/ci.yml,
and .claude/, then propose a concrete plan for gaps 1–5 for my approval before building.
```
