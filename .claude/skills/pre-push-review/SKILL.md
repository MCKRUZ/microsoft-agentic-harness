---
name: pre-push-review
description: "Use before every git push or gh pr create that touches src/ — runs /code-review, then /simplify, then (when the diff is security-sensitive) the security-reviewer agent, and records both review-gate receipts in one pass. Triggers: \"pre-push review\", \"get this ready to push\", \"run the review gate\", or any time you're about to push/open a PR and haven't run both required reviews against the current diff yet."
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

## Steps, in order

1. **Check scope.** Diff the current branch against its base for `src/**` files with a
   reviewable extension (`.cs .csproj .slnx .props .targets .razor .cshtml .ts .tsx .js
   .jsx .html` — see `.claude/hooks/review-scope.ps1` for the authoritative list). If
   nothing reviewable changed, say so and stop.

2. **Run `/code-review`** (Skill tool, skill `code-review`) against the current diff.
   Read its findings and fix anything HIGH or CRITICAL. If you changed code to fix a
   finding, re-run `/code-review` against the corrected diff before moving on — never
   record a receipt for a run whose findings you didn't act on.

3. **Record the code-review receipt** as its own Bash call — never chained with
   `git push` or with step 5's receipt call. The gate inspects the whole command string
   before anything runs, so a receipt written earlier in the same chained command is
   invisible to it:
   ```
   "<one-line summary of the code-review result>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review
   ```

4. **Run `/simplify`** (Skill tool, skill `simplify`) against the same diff and apply
   its fixes.

5. **Record the simplify receipt**, same pattern, its own Bash call:
   ```
   "<one-line summary of the simplify result>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind simplify
   ```

6. **Decide on `security-reviewer`.** This has no receipt of its own — it's a judgment
   call layered on top of the two mechanical gates, not something `review-gate.ps1`
   checks. Run it (Agent tool, `security-reviewer`) whenever the diff touches auth,
   identity, payments, secrets, or tool/agent governance — or when in doubt, ask
   `.github/scripts/security-gate-scope.sh --base <base-branch>` and launch it if
   `required=true`. Act on its findings before continuing.

7. **Re-verify after any fix.** If step 2, 4, or 6 caused a code change, the fingerprint
   the gate checks has changed. Repeat steps 2–5 (and 6, if still relevant) against the
   new diff. Do not record a receipt and then keep editing — the gate blocks on a
   fingerprint mismatch specifically to catch that.

8. **Report.** State plainly that both receipts are recorded for the current diff and
   the push is unblocked. Don't end by asking whether to run the reviews again or
   whether it's ready — that's the decision this skill exists to make for you.

## What this does not replace

- The full local test suite still has to be green before push — this skill is about
  the review gate, not the test gate.
- CI's `correctness-review` and `security-review` still run again server-side after the
  push. That's deliberate, not redundant with this skill: the local gate only fires for
  pushes made through Claude Code, so CI is the backstop for every other path a change
  can reach `main` — including a human pushing directly.
