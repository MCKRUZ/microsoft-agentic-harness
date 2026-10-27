---
name: pre-push-review
description: "Use before every git push or gh pr create that touches src/ — runs scripts/rails/run-gates.sh, /code-review, then /simplify, then (when the diff is security-sensitive) the security-reviewer agent, and records all three review-gate receipts in one pass. Triggers: \"pre-push review\", \"get this ready to push\", \"run the review gate\", or any time you're about to push/open a PR and haven't run the required reviews against the current diff yet."
---

# Pre-push review

Runs everything the local push gate (`.claude/hooks/review-gate.ps1`) requires, in one
pass, so no review step is left to memory across a long session.

## Why this exists

Before this skill, a session had to remember to invoke `/code-review` and `/simplify`
as two (or three, counting `security-reviewer`) separate manual steps, potentially many
messages apart. On 2026-08-13 a session ran `/code-review` and a security review twice
each but never ran `/simplify` at all — caught only because the push gate itself
refused the push. The gate already enforces that receipts *exist*; this skill makes it
trivial to actually produce them, in the right order, without depending on memory
across a long session.

The gate also requires a third receipt — `run-gates` — proving `scripts/rails/run-gates.sh`
ran clean locally. That one exists to stop a *fix → push → wait ~7 min for CI → fix again*
rhythm: every push to a PR branch re-triggers the remote correctness-review and grader
gates, so pushing after each small fix pays for a fresh Opus review cycle instead of
batching fixes and clearing them locally once. Running `run-gates.sh` here bundles that
batching into the same pass as the other two reviews, instead of it being a separate step
to remember.

## Steps, in order

1. **Check scope.** Diff the current branch against its base for `src/**` files with a
   reviewable extension (`.cs .csproj .slnx .props .targets .razor .cshtml .ts .tsx .js
   .jsx .html` — see `.claude/hooks/review-scope.ps1` for the authoritative list). If
   nothing reviewable changed, say so and stop.

2. **Run `scripts/rails/run-gates.sh` with no flags** (equivalent to `--fast`: build, test,
   OWASP, docs-links; against `origin/main` — falling back to `main` only if `origin/main`
   doesn't resolve) from the repo root. Do NOT pass `--all`: that adds local Opus
   correctness/security reviewers, measured at 2.5+ hours, that duplicate CI's required
   `correctness-review` and `security-review` checks. Fix anything it reports as `FAIL`,
   then re-run it — do not move on to step 3 until it prints "All selected gates passed."
   A clean run writes its own `run-gates` receipt automatically; you never write this one
   by hand. If `pwsh` isn't on this machine, say so and fall back to steps 3–6 plus a
   manual note that the run-gates receipt could not be produced — do not fabricate one.

3. **Run `/code-review` — round 1** (Skill tool, skill `code-review`) against the whole
   branch diff. Size the effort to the change: `medium` up to ~300 changed lines under
   `src/`, `high` up to ~1,000, `xhigh` above that. Then **triage every finding** into one
   tag before touching code:
   - `important` — would break behaviour, leak data, or block a rollback. Fix it.
   - `nit` — style, naming, small cleanups. Fix only when the fix is local and obvious;
     otherwise record `didnt-matter`. An agent that fixes every nit buys another commit and
     another review round for nothing — noise costs more when the author is an agent.
   - `pre-existing` — real, but not introduced by this diff. Do not fix it here; mention it
     in the PR body under Risk so it can become its own issue.

   For each `important` finding, **name its class** in one line (e.g.
   `ownership-check-bypassed`, `ambient-state-lost-across-yield`) and grep the diff for
   every other instance of that class. Fix all of them, not just the line the reviewer
   pointed at. If the change is a bug fix, also ask whether it addresses the root cause or
   a symptom.

4. **Record the code-review receipt** as its own Bash call — never chained with
   `git push` or with step 6's receipt call. The gate inspects the whole command string
   before anything runs, so a receipt written earlier in the same chained command is
   invisible to it. Write the round's findings to a JSON file first (schema in
   `.github/RAILS.md`, "Review conventions"; dispositions `fixed | wrong | intentional |
   didnt-matter`) so the noise rate can be measured:
   ```
   "<one-line summary of the code-review result>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review -Round <n> -FindingsFile <findings.json>
   ```
   Record it only once the round's `important` findings are fixed or dispositioned.

5. **Run `/simplify`** (Skill tool, skill `simplify`) against the same diff and apply
   its fixes.

6. **Record the simplify receipt**, same pattern, its own Bash call:
   ```
   "<one-line summary of the simplify result>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind simplify
   ```

7. **Decide on `security-reviewer`.** This has no receipt of its own — it's a judgment
   call layered on top of the mechanical gates, not something `review-gate.ps1` checks.
   Run it (Agent tool, `security-reviewer`) whenever the diff touches auth, identity,
   payments, secrets, or tool/agent governance — or when in doubt, ask
   `.github/scripts/security-gate-scope.sh --base <base-branch>` and launch it if
   `required=true`. Act on its findings before continuing.

8. **Re-verify after any fix — at most three rounds in total.** If step 3, 5, or 7 changed
   code, the fingerprint the gate checks has changed (the run-gates receipt from step 2
   included), so a new round is needed. Do not record a receipt and then keep editing — the
   gate blocks on a fingerprint mismatch specifically to catch that. Rounds 2 and 3 are
   narrower than round 1:
   - **Review only what the fixes changed.** Run `/code-review` on the fix commits (target
     them by range if the command accepts one; otherwise run it at `low` effort on the
     whole diff), and paste the earlier rounds' findings into the request under "already
     reported — do not repeat these; report only new defects or regressions".
   - **Do not re-review code that round 1 already passed** unless a fix touched it. On the
     Claude Code team, ~59% of what a looping reviewer kept re-flagging had been there since
     its first pass: it was finding a few more old issues each round, not new ones.
   - Repeat steps 2–6 (and 7, if still relevant) with `-Round 2` or `-Round 3`.

   **The cap.** If round 3 still leaves an `important` finding open, stop. Do not start a
   fourth round. Record the cap:
   ```
   "round 3: <n> Important findings still open: <one line each>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind round-cap -Round 3
   ```
   The gate then refuses the push. Tell Matt what is still open and what a different
   approach would look like. Push only if he says to push anyway, and record his words
   verbatim with `-Kind round-cap-ack`. Never write that receipt on your own judgment.

9. **Fill in the PR body before `gh pr create`** from `.github/PULL_REQUEST_TEMPLATE.md`.
   **Testing evidence** is the command you ran and its pasted output, not a claim. **How
   to revert** is the exact command. Include the `## Explainer` (see
   `.claude/rules/review-cadence.md`). End the body with the line `Review-rounds: <n>`
   (`cap` if the cap was hit), which the weekly telemetry reads.

10. **Report.** State plainly that all three receipts are recorded for the current diff,
    how many rounds it took, and that the push is unblocked. Don't end by asking whether
    to run the reviews again or whether it's ready — that's the decision this skill exists
    to make for you.

## What this does not replace

- The full local test suite still has to be green before push — this skill is about
  the review gate, not the test gate (`run-gates.sh` does run `dotnet test` as part of
  its default set, so a clean run covers this too).
- CI's `correctness-review` and `security-review` still run again server-side after the
  push. That's deliberate, not redundant with this skill: the local gate only fires for
  pushes made through Claude Code, so CI is the backstop for every other path a change
  can reach `main` — including a human pushing directly. `run-gates.sh` also cannot
  guarantee a fix is actually correct or complete; it only proves the same checks CI runs
  were run locally first, so the remote cycle should be a formality rather than where
  problems get discovered.
