# PR Plan — Two-Split Non-Regression Gate for Skill Training

**Status:** Plan only. No code written. Awaiting go-ahead.
**Source:** Self-Harness (Shanghai AI Lab, arXiv 2606.09498, Jun 2026). The one cheap, high-value
idea worth porting into our existing SkillOpt loop.
**Date:** 2026-06-22

---

## Problem

Our skill-training gate (`GateEvaluator.Evaluate`, `GateEvaluator.cs:47`) accepts a candidate skill
**iff its held-out (val) score strictly beats the current score**. It never checks whether the
candidate *regressed on the tasks the proposer was reflecting on* (the train / held-in split).

Consequence: an edit can win on val by noise while quietly breaking behavior that previously worked
on the held-in tasks, and we accept it. Single-split strict-improvement maximizes one number; it does
not protect against trading one split off against the other.

Self-Harness's distinguishing safety feature is a **two-sided non-regression gate**: re-evaluate the
candidate on **both** splits and accept only if neither regresses and at least one improves.

## The rule to port

Let `Δ_in = candidate_in − current_in` and `Δ_ho = candidate_ho − current_ho` (each a gate-metric
projected score). Accept iff:

```
Δ_in ≥ 0  AND  Δ_ho ≥ 0  AND  max(Δ_in, Δ_ho) > 0
```

"Best" continues to track the **held-out** score (the generalization metric), so `AcceptNewBest`
fires when an accepted candidate's held-out score also beats the running best.

## Why it's cheap here

Mapping to our existing loop (`TrainSkillCommandHandler.Handle`, `TrainSkillCommandHandler.cs:117`):

| Quantity | How we get it |
|---|---|
| `current_in`  | Score of `trainRollouts` — **already computed** each step (run on `currentSkill`, `:131`). Free. |
| `current_ho`  | Carried-forward `currentScore` (val) — **already tracked**. Free. |
| `candidate_ho`| `RolloutBatchScorer.Score(valRollouts)` — **already computed** (`:205`). Free. |
| `candidate_in`| Candidate re-scored on the **train** split — **the one new rollout per step**. |

So the entire cost of this safety upgrade is **one extra rollout batch per step** (candidate on the
train split). With fixed `Seed`, the train batch is deterministic, so `current_in` from
`trainRollouts` is stable and directly comparable to `candidate_in`.

## Design decision (needs your call inside the PR)

The single-split gate is not *wrong* — "maximize held-out, ignore held-in" is a legitimate policy
some consumers may want. The two-split gate is a genuinely different optimization policy, not a bug
fix. Per "replace, don't deprecate," I do **not** want a silent compatibility shim. Recommendation:

- Add an explicit `GateMode` enum: `StrictImprovementHeldOut` (today's behavior) and
  `TwoSplitNonRegression` (new).
- **Default to `TwoSplitNonRegression`** — it is strictly safer and matches the paper's evidence.
- Keep `StrictImprovementHeldOut` as a named, documented alternative, not a deprecated path.

This is the only judgment call; everything else is mechanical.

## Changes

### Domain
1. **`Domain.AI/SkillTraining/GateMode.cs`** (new enum) — `StrictImprovementHeldOut`,
   `TwoSplitNonRegression`. Full XML docs (template rule).
2. **`TrainSkillConfig.cs`** — add `GateMode GateMode { get; init; } = GateMode.TwoSplitNonRegression;`
3. **`GateResult.cs`** — add `HeldInScore` (candidate's projected held-in score) and
   `CurrentHeldInScore` so the step record / audit trail captures *why* a two-split decision was made.
   Keep existing fields (they remain the held-out lineage).

### Application
4. **`IGateEvaluator` / `GateEvaluator.cs`** — overload (or extend) `Evaluate` to accept the held-in
   pair (`candidateHardIn/SoftIn`, `currentHeldInScore`) and a `GateMode`. When mode is
   `StrictImprovementHeldOut`, behavior is bit-identical to today (held-in args ignored). When
   `TwoSplitNonRegression`, apply the Δ rule above. Preserve the existing ULP-stability contract:
   project each split's hard/soft *inside* the evaluator; never persist projected values.
5. **`TrainSkillCommandHandler.cs`** —
   - After `Apply`, in addition to the existing val rollout, run the candidate on a `"train"` split
     batch (`TrainBatchSize`, same `Seed`) and score it → `candidate_in`.
   - Compute `current_in` from this step's `trainRollouts` (already in hand).
   - Pass both pairs + `cfg.GateMode` into the gate.
   - On `AcceptNewBest` / `Accept`, carry forward both held-out and held-in current scores.
6. **`GateCandidateSkillCommand` / Handler / Validator** — thread the new params so the standalone
   gate command stays usable in isolation (it currently mirrors `Evaluate` 1:1).

### Tests (TDD — write first, must fail before impl)
7. **`GateEvaluatorTests.cs`** — new cases:
   - `Evaluate_TwoSplit_BothImprove_Accepts`
   - `Evaluate_TwoSplit_HeldOutUpHeldInDown_Rejects` (the regression this PR exists to catch)
   - `Evaluate_TwoSplit_BothFlat_Rejects` (max delta not > 0)
   - `Evaluate_TwoSplit_OneFlatOneUp_Accepts`
   - `Evaluate_StrictMode_IdenticalToLegacy` (parametrized parity guard against the old behavior)
8. **`TrainSkillCommandHandlerTests.cs`** — stub `IRolloutRunner` to return split-dependent scores;
   assert a held-out-up / held-in-down candidate is **rejected** under default config, and the extra
   train-split rollout is requested exactly once per step.

## Out of scope (explicit)
- No change to `SlowUpdate` / `MetaSkillUpdate` (epoch-boundary forgetting guard stays as-is — it is
  already *ahead* of Self-Harness).
- No broadening of the edit target beyond `SKILL.md`. That is the separate design note
  (`self-harness-full-harness-optimization.md`).

## Verification
`dotnet build src/AgenticHarness.slnx && dotnet test src/AgenticHarness.slnx`
Then `/code-review` + `/simplify` per review cadence; record receipts before push.

## Effort / risk
- **Effort:** ~1 focused session. Contained to one subsystem, no cross-layer ripple.
- **Risk:** Low. Default behavior changes (single→two-split) but that is the intended, safer policy;
  the parity test pins the legacy mode. One extra rollout/step is the only cost increase.
- **Forecloses:** nothing. The `GateMode` enum leaves room for future gate policies.
