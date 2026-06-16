#!/usr/bin/env bash
#
# apply-branch-protection.sh — apply the main-branch ruleset to GitHub as code.
#
# Part of the delivery rails (see .github/RAILS.md). This is "branch protection
# as code": the source of truth is .github/rulesets/main-branch-protection.json,
# and this script is the only sanctioned way to push it live. It is idempotent —
# it creates the ruleset if absent, updates it in place (by name) if present.
#
# It NEVER runs automatically. A human runs it deliberately, after reading the
# diff between the desired JSON and what is live. Applying live branch protection
# is a HIGH-risk, outward-facing action; treat it as such.
#
# Requirements: gh (authenticated, repo-admin scope), jq.
#
# Usage:
#   scripts/rails/apply-branch-protection.sh            # show plan, then apply
#   scripts/rails/apply-branch-protection.sh --dry-run  # show plan only, no writes
#   REPO=owner/name scripts/rails/apply-branch-protection.sh  # override target repo
#
set -euo pipefail

RULESET_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/.github/rulesets/main-branch-protection.json"
RULESET_NAME="$(jq -r '.name' "$RULESET_FILE")"
DRY_RUN=0

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

command -v gh >/dev/null || { echo "ERROR: gh CLI not found." >&2; exit 1; }
command -v jq >/dev/null || { echo "ERROR: jq not found." >&2; exit 1; }
[ -f "$RULESET_FILE" ] || { echo "ERROR: ruleset file missing: $RULESET_FILE" >&2; exit 1; }

# Resolve the target repo: $REPO override, else the current repo gh is pointed at.
REPO="${REPO:-$(gh repo view --json nameWithOwner --jq '.nameWithOwner')}"
echo "Target repository : $REPO"
echo "Ruleset name      : $RULESET_NAME"
echo "Source of truth   : $RULESET_FILE"
echo

# Find an existing ruleset with the same name (rulesets are not unique by name in
# the API, but we manage exactly one with this name). Fail LOUDLY if the lookup
# itself errors — a swallowed auth/rate failure would look like "no ruleset" and
# make us POST a duplicate instead of PUT-updating the existing one.
if ! RULESETS_JSON="$(gh api "repos/$REPO/rulesets" 2>&1)"; then
  echo "ERROR: failed to list rulesets for $REPO (auth/permission/rate?):" >&2
  echo "$RULESETS_JSON" >&2
  exit 1
fi
EXISTING_ID="$(printf '%s' "$RULESETS_JSON" | jq -r --arg n "$RULESET_NAME" \
  'if type == "array" then ([.[] | select(.name == $n) | .id][0] // "") else "" end')"

if [ -n "$EXISTING_ID" ]; then
  echo "Found existing ruleset id=$EXISTING_ID — will UPDATE (PUT)."
  METHOD="PUT"
  ENDPOINT="repos/$REPO/rulesets/$EXISTING_ID"
else
  echo "No existing ruleset named '$RULESET_NAME' — will CREATE (POST)."
  METHOD="POST"
  ENDPOINT="repos/$REPO/rulesets"
fi

echo
echo "--- Desired ruleset ---"
jq '{name, enforcement, conditions, bypass_actors, rules: [.rules[].type]}' "$RULESET_FILE"
echo "-----------------------"

if [ "$DRY_RUN" -eq 1 ]; then
  echo
  echo "DRY RUN: would $METHOD $ENDPOINT with the JSON above. No changes made."
  exit 0
fi

echo
read -r -p "Apply this ruleset live to $REPO? [y/N] " confirm
case "$confirm" in
  y|Y|yes|YES) ;;
  *) echo "Aborted. No changes made."; exit 0 ;;
esac

gh api --method "$METHOD" "$ENDPOINT" \
  -H "Accept: application/vnd.github+json" \
  --input "$RULESET_FILE" \
  --jq '{id, name, enforcement, html: ._links.html.href}'

echo
echo "Done. Verify on GitHub → Settings → Rules, or re-run with --dry-run to confirm parity."
