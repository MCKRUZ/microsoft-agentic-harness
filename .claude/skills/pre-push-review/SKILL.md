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

2. **Run `scripts/rails/run-gates.sh` with no flags** (equivalent to `--all`, against
   `main`) from the repo root. Fix anything it reports as `FAIL`, then re-run it — do not
   move on to step 3 until it prints "All selected gates passed." A clean run writes its
   own `run-gates` receipt automatically; you never write this one by hand. If `pwsh` or
   the `claude` CLI isn't on this machine, say so and fall back to steps 3–6 plus a manual
   note that the run-gates receipt could not be produced — do not fabricate one.

3. **Run `/code-review`** (Skill tool, skill `code-review`) against the current diff.
   Read its findings and fix anything HIGH or CRITICAL. If you changed code to fix a
   finding, re-run `/code-review` against the corrected diff before moving on — never
   record a receipt for a run whose findings you didn't act on.

4. **Record the code-review receipt** as its own Bash call — never chained with
   `git push` or with step 6's receipt call. The gate inspects the whole command string
   before anything runs, so a receipt written earlier in the same chained command is
   invisible to it:
   ```
   "<one-line summary of the code-review result>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review
   ```

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

8. **Re-verify after any fix.** If step 3, 5, or 7 caused a code change, the fingerprint
   the gate checks has changed — and that includes the run-gates receipt from step 2.
   Repeat steps 2–6 (and 7, if still relevant) against the new diff. Do not record a
   receipt and then keep editing — the gate blocks on a fingerprint mismatch specifically
   to catch that.

9. **Report.** State plainly that all three receipts are recorded for the current diff
   and the push is unblocked. Don't end by asking whether to run the reviews again or
   whether it's ready — that's the decision this skill exists to make for you.

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
