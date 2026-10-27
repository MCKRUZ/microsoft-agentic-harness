# Post-Change Review Cadence

## After Every Folder or Feature Completion
Run these two skills in order:

1. **`/code-review`** — Security and quality check. Catches hardcoded secrets, missing validation, mutation violations, structural issues. Blocks if CRITICAL or HIGH issues found.
2. **An explainer, in the PR body** — under `## Explainer`, at most 200 words written from the diff and the files it touches: what this slice of the code does, what the change does to it, and what would break if it were wrong. Matt reads this instead of the diff on tier-1 PRs, so it must describe the code as it now is, not the session's history. For a tier-2 change he may ask for a fuller HTML explainer in `.claude/reviews/` as well.

Do NOT skip these. Run them even when changes seem straightforward.

(Until 2026-10-27 step 2 was `/review-changes deep`, maintained through a `claude-code-reviewer`
skill and a `.claude/.session-manifest.json`. Neither skill existed in this repo, the global skill
set, or any installed plugin, so the step had never run: a documented control nothing invoked.)

## The review gate enforces this mechanically

This cadence is not an honor system. A `PreToolUse` hook (`.claude/hooks/review-gate.ps1`, wired
in `.claude/settings.json`) **blocks `git push` and `gh pr create`** when the branch's diff touches
reviewable source unless `/code-review`, `/simplify`, **and** a full local
`scripts/rails/run-gates.sh` pass have all been recorded against the exact **code** being pushed.
The `pre-push-review` skill runs all three in one pass — prefer it over doing the steps by hand.

- **Recording a code-review/simplify receipt:** pipe its summary to the helper, which binds the
  receipt to the reviewed code:
  `"<review summary>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review`
  (and again with `-Kind simplify`). Receipts live in the gitignored `.claude/.review-receipts/`.
- **Recording the run-gates receipt:** you don't — `scripts/rails/run-gates.sh` (no flags = `--fast`, default
  base `origin/main`, falling back to `main` only if that doesn't resolve) writes its own
  `-Kind run-gates` receipt automatically on a clean pass. This one
  exists specifically to stop a fix-then-push rhythm: every push re-triggers the remote
  correctness-review and grader gates, so pushing after each small fix pays for a fresh Opus
  review cycle instead of clearing checks locally once. `run-gates.sh` being the intended writer is
  a convention this hook enforces by making it the easy path, not a technical guarantee — an agent
  with shell access can still call the writer by hand, same as for the other two receipts. See
  `.github/RAILS.md`.
- **Re-arming is content-based, not commit-based.** Receipts are named after a fingerprint of the
  reviewable source diff (`.claude/hooks/review-scope.ps1`), so:
  - changing a single line of source re-arms the gate and forces a fresh review — as before;
  - committing docs, workflow YAML, or anything else non-reviewable **does not** discard the review.
    Under the old `HEAD`-SHA binding it did, which cost four review passes on PR #220 alone
    re-reading byte-identical source.
- **Reviewable source** is `src/**` with a `.cs .csproj .slnx .props .targets .razor .cshtml .ts
  .tsx .js .jsx .html` extension. `.tsx` matters: 175 tracked React components live under
  `src/Content/Presentation/**` and were **not** gated before 2026-08-02. `.json` is excluded on
  purpose so `package-lock.json` churn cannot force a full C# re-review.
- **Scope:** docs-, memory-, and config-only pushes pass without receipts.
- **Coverage boundary:** the hook only fires for pushes made *through Claude Code* — a human pushing
  from their own terminal is not gated (that is the server-side CI check's job). The hook stops the
  *agent* from skipping review.
- **Trust boundary:** a receipt's content is whatever was piped in, for all three kinds — the
  run-gates receipt is written by `run-gates.sh` itself rather than typed from memory, but nothing
  stops an agent from calling the same writer by hand, so it proves a receipt exists for this code,
  not that the underlying check was done, done well, or done at all. The non-forgeable enforcement
  is CI (`correctness-review`, `security-review`, `grader`, OWASP), which re-derives its own verdict
  server-side regardless of what any local receipt claims.
- **Emergency bypass:** set `RAILS_SKIP_REVIEW_GATE=1` (auditable; use sparingly).
- **Tests:** `pwsh -NoProfile -File .claude/hooks/tests/review-scope.tests.ps1` asserts the scoping
  and re-arm rules against real commits in this repo's history. Every suite under
  `.claude/hooks/tests/` runs in CI (`.github/workflows/hook-tests.yml`) when a hook changes.
- **Human-only decisions:** `.claude/hooks/human-only-guard.ps1` denies the agent posting a tier-2
  `Reviewed-by: @MCKRUZ` sign-off, applying an `accepted-risk:*` label, re-arming the
  `rails:breaker` issue, or merging any PR not labelled `tier:0`. When one of these is the next
  step, tell Matt and stop; do not look for another route.

## After a Full Layer is Complete
Run this additional skill:

3. **`/simplify`** — Cross-file analysis for reuse opportunities, dead code, and efficiency improvements. Only meaningful when multiple files exist to compare against.

## Fix-Review Cycle
When `/code-review` finds HIGH issues:
1. Present findings to user
2. Get approval on fix approach
3. Apply fixes — every instance of the finding's class, not only the flagged line
4. Re-run `/code-review` on what the fixes changed, with the earlier findings marked
   "already reported — do not repeat"
5. Update the PR body's `## Explainer` if the fixes changed what the code does

**Review must converge: three rounds at most.** If the third round still has an Important
finding open, stop and tell Matt the approach may be wrong, rather than starting a fourth. The
`pre-push-review` skill writes a `round-cap` receipt and the push gate refuses until Matt says to
push anyway. (Anthropic's Claude Code team saw one PR run 50+ rounds overnight; after capping
rounds and re-reviewing only new diffs, PRs needing five or more rounds fell from 8.9% to 1.5%.)
