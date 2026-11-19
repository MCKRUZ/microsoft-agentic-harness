#!/usr/bin/env bash
#
# check-rail-labels.sh — fail when a label the rails depend on does not exist.
#
# A workflow that keys on a label (`risk:high`, `accepted-risk:*`, `tier:*`, `rails:breaker`)
# silently never fires when the label is missing: `gh pr edit --add-label` refuses a label that
# does not exist, so the trigger cannot even be applied. Until 2026-10-27 `risk:high` and
# `accepted-risk:security` were exactly that — referenced by security-review and its rubric,
# never created. This turns a missing label into a red scheduled run instead of a dead trigger.
#
# The list mirrors .github/RAILS.md "Review conventions"; change both together.
#
# Usage: .github/scripts/check-rail-labels.sh [owner/repo]   (needs an authenticated gh)
set -euo pipefail

REPO="${1:-${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}}"
REQUIRED=(
  "risk:high"
  "accepted-risk:correctness"
  "accepted-risk:security"
  "tier:0"
  "tier:1"
  "tier:2"
  "rails:breaker"
)

EXISTING="$(gh label list --repo "$REPO" --limit 500 --json name --jq '.[].name')"
missing=0
for label in "${REQUIRED[@]}"; do
  if ! grep -qxF -- "$label" <<<"$EXISTING"; then
    echo "::error::Label '$label' is missing from $REPO. A rail keys on it (see .github/RAILS.md, Review conventions); recreate it."
    missing=$((missing + 1))
  fi
done

if [ "$missing" -gt 0 ]; then
  echo "$missing rail label(s) missing."
  exit 1
fi
echo "All ${#REQUIRED[@]} rail labels exist in $REPO."
