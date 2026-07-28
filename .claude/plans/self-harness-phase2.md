# Self-Harness Phase 2 — Widen the Optimizer's Edit Target (Fenced)

**Status:** Approved by Matt 2026-06-23. Build in two steps (two PRs).
**Prerequisite:** Phase 1 fence merged (PR #78) — `HarnessSurface`, `EditableSurfaceRegistry`,
`HarnessPatchValidator`, `Edit.Surface`. All present on `main`.
**Source:** `self-harness-full-harness-optimization.md` (design memo). This plan refines that memo's
Phase 2 with four guardrails Matt required.

---

## Plain-language goal

Today the self-improvement loop may edit exactly one document (a SKILL.md). This widens it to a few
more surfaces — but every new surface is locked by default, and the riskiest one (a runtime settings
dial) can only ever *suggest* a change, never make it.

## Matt's four guardrails (the acceptance criteria)

1. **Locked by default; humans only unlock.** No new surface is editable out of the box. Unlocking is
   a deliberate human edit to the code-owned registry. The agent can never unlock a surface itself.
   Governance surfaces (DeniedTools / autonomy tier / content-safety / the registry itself) stay
   frozen-by-construction from Phase 1 — not even a human edit can mark them editable.
2. **Audit everything.** Applies, suggestions, and fence-rejections all write to the existing
   tamper-evident hash-chained JSONL audit. Nothing happens invisibly.
3. **Suggestion-only path.** A per-surface mode: `AutoApply` vs `SuggestOnly`. In `SuggestOnly` the
   loop runs the full propose→test→gate, but emits an audited *suggestion* instead of mutating the
   surface. The settings dial defaults to `SuggestOnly`.
4. **Granular, explicit locks.** For config surfaces, the fence is field-level + value-bounded — e.g.
   the dial may change *only* the retry count, *only* within a set range; never the delay or backoff
   style. Out-of-bounds edits are rejected below the gate, before they can be scored.

---

## Step 1 (PR A) — Three prose surfaces, auto-apply

**Surfaces:** `ArtifactGuidance`, `FailureRecovery`, `VerificationPrompt` (all already exist in the
`HarnessSurface` enum). These are prose, so they reuse the existing text `PatchApplier` — no new apply
machinery. They may auto-apply because they are low-stakes, fenced, two-split-gated, and audited.

**Surface location decision (flagged):** these live as optional **named sections inside the skill
document** the loop already optimizes (`## Artifact Guidance`, `## Failure Recovery`,
`## Verification`), reusing `SkillDefinition`'s existing section-parsing precedent
(`## Objectives` / `## Trace Format`). This keeps Step 1 skill-scoped and avoids sectioning the
monolithic `AgentManifest.Instructions` (a bigger, lower-payoff lift). **Alternative not taken:**
AGENT.md-level prose sections (truly harness-level, faithful to the paper, but requires building an
AGENT.md section parser + write-back). Left as a future option; easy to add later.

**Changes:**
1. `TrainSkillConfig.TargetSurface` (default `SkillDocument`) — which surface this run optimizes.
   FluentValidation: must be an editable surface per the registry.
2. Pure surface resolver/splicer: given the full document + `TargetSurface`, extract the section's
   text (whole doc for `SkillDocument`), and reinsert the optimized section back. Stateless helper,
   unit-tested for extract/missing-section/reinsert idempotence.
3. Register the three prose surfaces as editable via the code-owned registry (DI composition).
   Default registry stays SkillDocument-only; widened registry is the opt-in.
4. Tag every emitted `Edit` with the run's `TargetSurface` so `HarnessPatchValidator` sees the right
   surface (the fence already runs at intake, below the gate).
5. Audit on apply: extend the optional `IGovernanceAuditService` path to record accepted applies
   (rejections already audited in Phase 1). Try/catch isolated — audit never blocks the control.
6. Tests: surface validation, fence accepts editable / rejects frozen, splicer round-trip, end-to-end
   loop run targeting a prose section.

## Step 2 (PR B) — The retry dial, suggestion-only BY DESIGN, locked + bounded

**Surface:** `ToolErrorRetryLimit` (exists in enum). Backed by `RetryConfig.MaxAttempts`
(`Domain.Common/Config/AI/Resilience/RetryConfig.cs`, default 2) — an integer, not text.

**Design decision (Matt, 2026-06-23): suggestion-by-design — NO apply path for the dial.** The
loop scores candidates only by running the skill through rollouts; a retry-limit is plumbing
behavior, not skill text, so it cannot flow through the rollout-scoring gate at all. Combined with the
"suggestion-only" guardrail, the dial therefore needs **no config-patching/apply engine** (the
original plan's `ConfigPatchApplier` is dropped as YAGNI). It only needs a bounded, audited
*suggestion-emission* path that a human reviews. The dial never mutates live config.

**Changes:**
1. **`HarnessChangeSuggestion`** (Domain.AI/SkillTraining): record { Surface, Field, CurrentValue,
   ProposedValue, Rationale }. The loop/reflection emits these; they are never auto-applied.
2. **`ConfigSurfaceConstraint`** (code-owned): { Surface, AllowedFields, Min, Max } — for the dial,
   `AllowedFields = [MaxAttempts]`, bounds e.g. `[2, 5]`; `BaseDelaySeconds`/`BackoffType` frozen.
   Human-only, in code, never agent-reachable.
3. **`HarnessSuggestionValidator`** (or extend the fence): rejects any suggestion whose surface is not
   suggestion-enabled, whose field is not in `AllowedFields`, or whose value is out of `[Min, Max]`.
   Rejected suggestions are audited; accepted ones are audited and surfaced — neither mutates config.
4. **Surfacing + audit:** valid suggestions written to the tamper-evident trail
   (`skill_training.harness_change_suggested`) and exposed for human review (e.g. on the run result).
5. **Human-only unlock** end-to-end: surface enablement + constraints all in code/DI composition. No
   config-file or agent-reachable path widens them. Default: dial suggestions disabled.
6. Tests: bounds enforcement (reject < min / > max / wrong field / frozen field), valid suggestion is
   emitted + audited + never mutates `RetryConfig`, disabled-by-default, audit event recorded.

---

## Out of scope (stays deferred — unchanged)

- **Phase 3** high-stakes surfaces (`ToolAvailability`, `MemoryScopeRules`): only ever behind the
  existing AllOf/AnyOf/Quorum human escalation workflow. Not built here.
- Governance surfaces remain frozen-by-construction. Never editable.

## Rollback story

Prose edits: two-split gate + checkpoint revert (existing). Dial: suggestion-only means a bad idea
never ships without a human, so there is nothing to roll back automatically.
