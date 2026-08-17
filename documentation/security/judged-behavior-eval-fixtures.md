# Fixtures for Judged-Behaviour Evals

> Date: 2026-08-16. Target: .NET 10 (Microsoft Agentic Harness), `llm_judge` metric family. Audience: harness engineers and template consumers authoring conduct/behaviour rubrics for `eval-datasets/*.yaml`.

## 1. Why this exists

An LLM judge that only ever sees "positive" and "negative" cases — the agent behaved, the agent didn't — cannot tell a working rubric from a broken one. A judge that has quietly collapsed into grading the *answer* instead of the *process* looks identical to a healthy one on that fixture set: both pass the positive case, both fail the negative case. The one thing that tells them apart is a case designed to be **right for the wrong reason** — a case a collapsed judge passes and a working one fails.

This document is the standard fixture matrix for any `llm_judge` case grading agent conduct rather than answer correctness, and the discipline for changing one once the suite is running. It is the authoring counterpart to the mechanical enforcement added for [#335/#334/#336](../../eval-datasets/seed/governance-sanitization.yaml): the strict verdict contract (`verdict_contract: strict`), trajectory visibility (`trajectory: tools,governance`), and the answer-key opt-out (`include_expected_output: "false"`) documented in `Presentation.EvalRunner/README.md`.

## 2. The fixture matrix

| Fixture | What it proves | If missing |
|---|---|---|
| **Positive** | The trigger occurs and the expected conduct is visible. | No proof the rubric can ever pass a real case. |
| **Negative** | The trigger occurs and the required conduct is missing. | No proof the rubric can ever fail a real case. |
| **Lucky-correct negative** | The final output is correct, but the required *process* was not followed — must still fail. | The most damaging gap: a rubric can look correct on positive/negative alone while actually grading the answer, not the behaviour it claims to grade. |
| **Outside scope** | The trigger never occurs; the behaviour is simply not exercised. | The rubric can't be told "not applicable" from "failed," and starts contaminating pass-rate statistics for cases that were never a real test. |
| **Allowed boundary** | A permitted alternative path that must **not** be penalised. | The rubric over-fits to one implementation of compliant behaviour and starts failing legitimate variations. |

Every conduct rubric added to a merge-gating suite should have a positive and a negative case at minimum. A rubric grading a process claim (governance, escalation, ordering, tool choice) — not just a text pattern — needs the lucky-correct-negative too, because that is specifically the fixture that would catch the rubric silently degrading into answer-matching.

`eval-datasets/seed/governance-sanitization.yaml` case `gov-san-09-lucky-refusal-without-governance` is the reference example: its input and expected refusal text are identical to the positive case (`gov-san-08-refuses-write-secrets`), and it is designed to fail anyway because the rubric requires the write attempt to have been gated through the governed tool path — a requirement `include_expected_output: "false"` and `trajectory: "tools,governance"` make it possible to check at all.

## 3. Authoring discipline

**Change one boundary at a time.** A rubric edit and a fixture edit in the same commit make a resulting pass/fail flip undiagnosable — you no longer know whether the rubric got stricter, the fixture got easier, or both.

**The negative case should be plausible, not comically bad.** A negative fixture engineered to be obviously wrong tests whether the judge can read; it does not test whether the rubric is well-specified. Make it resemble a run a real agent could actually produce.

**A missing trajectory is not a pass.** If a rubric depends on `{{governance}}` or `{{tools_invoked}}` and that data was never recorded for the run, the metric returns `Verdict.Warn` before the judge is even invoked — never a silent `Pass`. See `GovernanceTraceRenderer.IsEngaged` (`Application.AI.Common/Evaluation/Governance/`) and its caller in `LlmJudgeMetric.ScoreAsync`.

## 4. Diagnosing a disagreement

When a case's expected verdict and observed verdict differ, the cause is exactly one of five things. Name which one before touching anything:

1. **Rubric wording** — the requirement as written doesn't say what you meant it to say.
2. **Fixture** — the case's input/trajectory doesn't actually exercise what the rubric claims to test.
3. **Judge** — the model itself reasoned incorrectly against a correctly-specified rubric and fixture.
4. **Telemetry** — the trajectory data (`ToolsInvoked`, `GovernanceTrace`) wasn't captured correctly for this run, independent of the agent's actual behaviour.
5. **Policy** — the rubric is enforcing a requirement the team no longer wants enforced.

**Do not contort the rubric wording to compensate for a leaked fixture or a broken judge.** If the cause is telemetry, fix the capture path. If the cause is the fixture, fix the fixture. Editing the layer that's easiest to edit, rather than the layer that's actually wrong, is how a suite accumulates rubrics nobody can explain a year later — the same failure mode the project's mutation-testing standard exists to catch in ordinary unit tests, applied here to judged behaviour.

## Related

- `Presentation.EvalRunner/README.md` — the `llm_judge` metric's parameter reference (`verdict_contract`, `trajectory`, `include_expected_output`).
- [`owasp-agentic-top-10-evals.md`](./owasp-agentic-top-10-evals.md) — the OWASP eval pack is deliberately **mechanical-predicate-only** for merge gating (no LLM-as-judge in the gate signal); this document is for the separate, judged conduct/governance suites where an LLM judge is the scoring mechanism itself.
