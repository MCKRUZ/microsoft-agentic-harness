# Post-Change Review Cadence

## After Every Folder or Feature Completion
Run these two skills in order:

1. **`/code-review`** — Security and quality check. Catches hardcoded secrets, missing validation, mutation violations, structural issues. Blocks if CRITICAL or HIGH issues found.
2. **`/review-changes deep`** — Narrative HTML report explaining the *why* behind changes. Generates a self-contained report in `.claude/reviews/`. Open for the user to review.

Do NOT skip these. Run them even when changes seem straightforward.

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
