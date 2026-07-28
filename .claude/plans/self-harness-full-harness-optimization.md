# Design Note — Should the Optimizer Ever Edit the Full Harness?

**Status:** Design note / decision memo. No commitment, no code. For discussion.
**Source:** Self-Harness (arXiv 2606.09498). Their optimizer edits the *whole* deployment
scaffolding, not just one prose document. This note asks whether we should follow, and if so,
behind what fences.
**Date:** 2026-06-22

---

## The idea in one line

Today our self-improvement loop (`TrainSkill`) may edit exactly one artifact: a `SKILL.md` document.
Self-Harness points the same propose→evaluate→accept loop at the **entire harness surface** — system
prompt, tool list, memory/state rules, verification rules, and runtime control policies. The question
on the table is whether to widen our optimizer's edit target the same way.

## Why it's tempting

- It is where most of Self-Harness's reported gains came from: their biggest wins were edits to
  *runtime control policies* (tool-error limits, recovery middleware) and *tool availability*, not
  prose tweaks. A prose-only optimizer leaves that lever untouched.
- We already own every surface they edit. `AGENT.md` declares tools, state config, and decision
  frameworks. We have runtime control policies, verification, and memory rules as first-class
  concepts. The loop, the bounded-edit machinery (`Edit{Op,Target,Content}`, `PatchApplier`), the
  gate, and the audit lineage already exist. The missing piece is "what can a patch legally target."

## Why it is NOT a quick win — the governance problem

This is the crux, and why this is a memo and not a PR plan.

Letting an automated loop edit `AGENT.md` / tool declarations / runtime policies means **the
optimizer can edit the very things our safety architecture is designed to lock down**:

1. **Plugin boundary governance** (`AllowedTools` / `DeniedTools` / `AutonomyLevel`). `DeniedTools`
   is documented as *bypass-immune* — it cannot be overridden by auto-approve modes. An optimizer
   that can edit tool declarations could propose re-enabling a denied tool. The gate as it stands
   (pass-rate non-regression) would happily accept that if it improved the score.
2. **Autonomy tiers** (Manual / Supervised / Autonomous, enforced via MediatR pipeline behavior). A
   harness edit could propose loosening the tier. Self-improvement must never be able to escalate its
   own autonomy.
3. **Content safety wiring** (always through `AgentFactory`). A harness edit must not be able to
   remove a safety filter to "pass more tasks."
4. **The paper itself flags this.** Their stated limitation: pass-rate non-regression "would require
   stronger acceptance gates than pass-rate alone" for higher-stakes harness changes. They did not
   solve the governance problem; they bounded their setting to avoid it.

So the real work here is **not** wiring the optimizer to more surfaces. It is designing the
**fence**: an immutable allowlist of what a harness patch may touch, enforced *below* the gate so it
cannot be optimized around.

## Proposed shape (IF we do this)

A phased design, each phase shippable and independently valuable:

### Phase 1 — Editable-surface registry (the fence)
- A declarative, **code-owned** (not LLM-editable) registry of which harness surfaces are
  optimizable and which are frozen. Frozen by construction: `DeniedTools`, autonomy tier, content
  safety config, the registry itself.
- A `HarnessPatchValidator` that runs **before** the gate and hard-rejects any patch touching a
  frozen surface — independent of whether the patch improves the score. This is the analog of the
  bypass-immune `DeniedTools` rule, applied to self-modification.
- Every rejected-by-fence patch is logged to the existing JSONL audit trail with the surface it tried
  to touch. (Tamper-evident hash-chain already exists — reuse it.)

### Phase 2 — Widen the edit target to *safe* surfaces only
- Allow patches to: artifact-creation guidance, failure-recovery instructions, verification prompts,
  tool-error retry limits — the low-stakes runtime-policy surfaces that drove Self-Harness's gains.
- Reuse the two-split non-regression gate (see `two-split-nonregression-gate.md`) as the quality bar.
- Still **no** edits to tool availability or memory-scope rules in this phase.

### Phase 3 — Higher-stakes surfaces behind a human gate
- Tool-availability and memory-rule edits, if ever, go through an **escalation workflow** (we already
  have AllOf/AnyOf/Quorum escalation with JSONL audit + AG-UI notification). The optimizer *proposes*;
  a human *approves*. The loop never self-applies a high-stakes harness change.

## What I'd want answered before any of this is built

1. **Appetite:** is "an agent that rewrites its own AGENT.md/tools" a direction we want this template
   to endorse at all? It is a strong claim for an enterprise template that consumers clone. The
   conservative answer ("the optimizer tunes skill prose; humans own the harness") is defensible and
   may be the *right* product stance regardless of feasibility.
2. **Stakes boundary:** where exactly is the line between "safe to auto-apply" and "needs a human"?
   My Phase 2/3 split is a proposal, not a given.
3. **Blast radius of a bad accept:** even fenced, a bad runtime-policy edit ships into every agent
   turn until the next gate. What's the rollback story? (We have checkpoints; is checkpoint-revert
   enough, or do we need a canary?)

## Recommendation

**Do not start this as code.** Ship the two-split gate first (cheap, strictly good). Treat full-harness
optimization as a **roadmap candidate gated on a product decision**, not an engineering task. If we
pursue it, Phase 1 (the fence) is mandatory and must land *before* Phase 2 — widening the edit target
without the surface registry would let the optimizer edit governance, which is a non-starter.

My honest lean: the *fence* (Phase 1) is worth building regardless, because it makes even today's
single-document optimizer auditable about what it may touch. Phases 2–3 are a genuine "do we want
self-modifying agent config" decision that is yours, not mine, to make.

## Related
- `two-split-nonregression-gate.md` — the prerequisite gate upgrade.
- Memory: `project_skill_training_subsystem.md`, `project_mcp_hardening.md` (escalation + audit
  hash-chain we'd reuse).
