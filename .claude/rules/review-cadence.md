# Post-Change Review Cadence

## After Every Folder or Feature Completion
Run these two skills in order:

1. **`/code-review`** — Security and quality check. Catches hardcoded secrets, missing validation, mutation violations, structural issues. Blocks if CRITICAL or HIGH issues found.
2. **`/review-changes deep`** — Narrative HTML report explaining the *why* behind changes. Generates a self-contained report in `.claude/reviews/`. Open for the user to review.

Do NOT skip these. Run them even when changes seem straightforward.

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
- **Recording the run-gates receipt:** you don't — `scripts/rails/run-gates.sh` (no flags, default
  base `main`) writes its own `-Kind run-gates` receipt automatically on a clean pass. This one
  exists specifically to stop a fix-then-push rhythm: every push re-triggers the remote
  correctness-review and grader gates, so pushing after each small fix pays for a fresh Opus
  review cycle instead of clearing checks locally once. Because the script writes it, not the
  agent, it can't be produced without the gates having actually run — see `.github/RAILS.md`.
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
- **Trust boundary:** for code-review/simplify, a receipt's content is whatever was piped in, so the
  gate proves a review was recorded for this code, not that it was done well. The non-forgeable
  enforcement there is CI (`correctness-review`, `security-review`, `grader`, OWASP), which
  re-derives its verdict server-side. The run-gates receipt is stronger evidence — it's written by
  the script itself only on an actual passing run, so it proves the local gates genuinely ran, even
  though it still can't guarantee the fix underneath is correct.
- **Emergency bypass:** set `RAILS_SKIP_REVIEW_GATE=1` (auditable; use sparingly).
- **Tests:** `pwsh -NoProfile -File .claude/hooks/tests/review-scope.tests.ps1` asserts the scoping
  and re-arm rules against real commits in this repo's history.

## After a Full Layer is Complete
Run this additional skill:

3. **`/simplify`** — Cross-file analysis for reuse opportunities, dead code, and efficiency improvements. Only meaningful when multiple files exist to compare against.

## Fix-Review Cycle
When `/code-review` finds HIGH issues:
1. Present findings to user
2. Get approval on fix approach
3. Apply fixes
4. Re-run `/code-review` to verify clean build + 0 warnings
5. Then run `/review-changes` to capture the full story including fixes

## Session Manifest
The `claude-code-reviewer` skill maintains `.claude/.session-manifest.json` to track intent behind changes. After every `Edit` or `Write` tool call, update the manifest with the change intent. This enables faster, more accurate review generation.
