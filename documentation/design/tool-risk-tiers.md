# Tool Risk Tiers & Graded Autonomy

> Date: 2026-06-18. Audience: harness engineers and template consumers configuring how tool blast radius drives approval. Phase 3 of the MCP/governance hardening line.

## 1. Why

Before this change, the governance pipeline treated every tool the same when deciding approval severity: a tool that deletes files and one that reads a document both escalated at a fixed `RiskLevel.Medium`. There was no signal connecting a tool's actual impact to the autonomy/approval machinery, even though the harness already had a graded-autonomy engine (`IAutonomyDecisionEvaluator`) that maps a `BlastRadius` to an auto-approve / require-approval decision per tier — it was only wired to the change-proposal pipeline.

This phase gives each tool a declared blast radius and feeds it into both the approval **decision** and the escalation **severity**.

## 2. The model

A tool declares its intrinsic worst-case impact via `ITool.RiskTier`, reusing the existing `BlastRadius` scale (no new enum, no mapping layer to the evaluator):

| Band | Meaning for a tool | Example tools (defaults shipped) |
| --- | --- | --- |
| `Trivial` | No real-world effect | `echo_lookup`, `echo_calculate` |
| `Low` | Read-only / preview | `read_file`, `list_files`, `document_search`, `iac_scan`, `iac_plan`, `iac_generate`, `k8sgpt_analyze`, `detect_drift`, `cluster_health`, `run_lint` |
| `Medium` | Bounded writes / sandboxed execution | `restricted_search`, `run_tests`, `document_ingest`, **and the default for any tool that does not override `RiskTier`** |
| `High` | File mutation, GitOps proposals, subagent delegation | `write_file`, `file_system`, `propose_remediation`, `delegate_task` |
| `Critical` | Production availability / security boundaries | (none shipped by default) |

`RiskTier` is a default-implemented interface member (`=> BlastRadius.Medium`), so every existing and future tool compiles unchanged and is treated as Medium until it classifies itself. The rating is tool-level (worst-case across operations); per-operation refinement is a separate future concern.

## 3. Two consumption points

**Escalation severity (always on).** When a tool call is routed to approval, `GovernancePolicyBehavior` derives the escalation `RiskLevel` from the tool's blast radius (`BlastRadiusRiskMapping.ToRiskLevel`) instead of a fixed `Medium`. A `High` tool now gets the stricter approval strategy, shorter timeout, and wider notification its severity warrants. This only changes the *severity label* of an escalation that was already happening, so it is safe to enable unconditionally.

**Approval decision (opt-in).** `ToolPermissionBehavior` layers a risk gate on top of the rule-based permission decision: it resolves the tool's risk (`IToolRiskClassifier`) and asks the existing `IAutonomyDecisionEvaluator` whether that blast radius may auto-approve under the active tier. The gate can only **tighten** — `Allow → Ask` (RequiresApproval) or `Allow → Deny` (Forbidden) — never loosen. A `Deny`/`Ask` from the rules is left untouched.

### Safety: off by default

The approval gate is active only when graded autonomy is explicitly enabled (`AI:Permissions:GradedAutonomy:Enabled = true`). With it off — the default — the gate is a no-op and the evaluator is never consulted, so **shipping tool risk ratings alone changes no runtime behavior**. A consumer turns the gate on deliberately and configures the per-tier `PerBlastRadius` table that decides which bands auto-approve.

The evaluator already enforces two load-bearing invariants that now apply to tool calls for free: **Critical always requires approval** regardless of tier, and **state-changing tools** (`!ITool.IsReadOnly`) default to requiring approval unless explicitly opted in.

## 4. Unknown tools

`IToolRiskClassifier` resolves the registered `ITool` for a name and reads its `RiskTier` / `IsReadOnly`. A name that does not resolve — an external MCP tool, a typo — returns `ToolRiskProfile.Default` (`Medium`, treated as a state change). The default never classifies a tool as *lower* risk than reality, so an unrecognized tool is never silently waved through at a looser bar.

## 5. Implementation map

| Concern | Type | Project |
| --- | --- | --- |
| Per-tool blast radius | `ITool.RiskTier` (default `Medium`) | `Application.AI.Common` |
| Risk resolution by name | `IToolRiskClassifier` / `ToolRiskClassifier` | `Application.AI.Common` |
| Blast radius → escalation severity | `BlastRadiusRiskMapping.ToRiskLevel` | `Domain.AI` |
| Escalation severity wiring | `GovernancePolicyBehavior` | `Application.AI.Common` |
| Approval gate (opt-in) | `ToolPermissionBehavior` → `IAutonomyDecisionEvaluator` | `Application.AI.Common` |
| Graded-autonomy decision table | `AutonomyTierPolicy.PerBlastRadius` (config) | `Domain.AI` / config |
