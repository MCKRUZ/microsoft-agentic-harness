# Post-Change Review Cadence

## After Every Folder or Feature Completion
Run these two skills in order:

1. **`/code-review`** — Security and quality check. Catches hardcoded secrets, missing validation, mutation violations, structural issues. Blocks if CRITICAL or HIGH issues found.
2. **`/review-changes deep`** — Narrative HTML report explaining the *why* behind changes. Generates a self-contained report in `.claude/reviews/`. Open for the user to review.

Do NOT skip these. Run them even when changes seem straightforward.

## The review gate enforces this mechanically

This cadence is not an honor system. A `PreToolUse` hook (`.claude/hooks/review-gate.ps1`, wired
in `.claude/settings.json`) **blocks `git push` and `gh pr create`** when the branch's diff touches
compilable source (`src/**`) unless `/code-review` **and** `/simplify` have been recorded against the
exact `HEAD` commit being pushed.

- **Recording a review:** pipe its summary to the helper, which binds the receipt to the current
  commit:
  `"<review summary>" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review`
  (and again with `-Kind simplify`). Receipts live in the gitignored `.claude/.review-receipts/`.
- **Re-arming:** amending or adding commits changes `HEAD`, so the gate demands a fresh review of the
  final code. Run the reviews on the commit you actually push.
- **Scope:** docs-, memory-, and config-only pushes pass without receipts.
- **Coverage boundary:** the hook only fires for pushes made *through Claude Code* — a human pushing
  from their own terminal is not gated (that is the server-side CI check's job). The hook stops the
  *agent* from skipping review.
- **Emergency bypass:** set `RAILS_SKIP_REVIEW_GATE=1` (auditable; use sparingly).

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
