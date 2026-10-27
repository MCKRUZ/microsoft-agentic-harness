#!/usr/bin/env bash
#
# apply-branch-protection.sh — apply the main-branch rulesets to GitHub as code.
#
# Part of the delivery rails (see .github/RAILS.md). This is "branch protection
# as code": the source of truth is every .github/rulesets/*.json, and this script is
# the only sanctioned way to push them live. It is idempotent — each ruleset is
# created if absent and updated in place (matched by name) if present.
#
# Two rulesets, split on purpose (RAILS.md, "Risk tiers"):
#   main-checks            — required checks (incl. tier-gate), review-thread resolution,
#                            no deletion, no force-push. NO bypass actors: not even the
#                            admin can merge a red PR.
#   main-branch-protection — the human-approval rule, with the admin bypass a solo repo
#                            needs (GitHub forbids approving your own PR).
# With a single ruleset, the admin bypass also skipped the required checks.
#
# It NEVER runs automatically. A human runs it deliberately, after reading the
# diff between the desired JSON and what is live. Applying live branch protection
# is a HIGH-risk, outward-facing action; treat it as such.
#
# RECOVERY. Because main-checks has no bypass, a rail that cannot report (for example an
# expired CLAUDE_CODE_OAUTH_TOKEN, which makes the AI reviewers fail closed) blocks every
# merge, the admin's included. Repository admins can always edit rulesets, so the way out is:
#   scripts/rails/apply-branch-protection.sh --disable main-checks   # merge the fix, then
#   scripts/rails/apply-branch-protection.sh --enable main-checks
# Disabling is visible in the repository's rule history; re-enable as soon as the fix lands.
#
# Requirements: gh (authenticated, repo-admin scope), jq.
#
# Usage:
#   scripts/rails/apply-branch-protection.sh             # show plan, then apply all
#   scripts/rails/apply-branch-protection.sh --dry-run   # show plan only, no writes
#   scripts/rails/apply-branch-protection.sh --disable <name> | --enable <name>
#   REPO=owner/name scripts/rails/apply-branch-protection.sh  # override target repo
#
set -euo pipefail

RULESET_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/.github/rulesets"
DRY_RUN=0
TOGGLE=""
TOGGLE_NAME=""

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY_RUN=1; shift ;;
    --disable|--enable) TOGGLE="${1#--}"; TOGGLE_NAME="${2:?--$TOGGLE needs a ruleset name}"; shift 2 ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

command -v gh >/dev/null || { echo "ERROR: gh CLI not found." >&2; exit 1; }
command -v jq >/dev/null || { echo "ERROR: jq not found." >&2; exit 1; }

REPO="${REPO:-$(gh repo view --json nameWithOwner --jq '.nameWithOwner')}"
echo "Target repository : $REPO"

# Fail LOUDLY if the lookup itself errors — a swallowed auth/rate failure would look like
# "no ruleset" and make us POST a duplicate instead of PUT-updating the existing one.
if ! RULESETS_JSON="$(gh api "repos/$REPO/rulesets" 2>&1)"; then
  echo "ERROR: failed to list rulesets for $REPO (auth/permission/rate?):" >&2
  echo "$RULESETS_JSON" >&2
  exit 1
fi
id_for() {
  printf '%s' "$RULESETS_JSON" | jq -r --arg n "$1" \
    'if type == "array" then ([.[] | select(.name == $n) | .id][0] // "") else "" end'
}

if [ -n "$TOGGLE" ]; then
  ID="$(id_for "$TOGGLE_NAME")"
  [ -n "$ID" ] || { echo "ERROR: no live ruleset named '$TOGGLE_NAME'." >&2; exit 1; }
  STATE=$([ "$TOGGLE" = "disable" ] && echo disabled || echo active)
  read -r -p "Set ruleset '$TOGGLE_NAME' (id $ID) enforcement to '$STATE' on $REPO? [y/N] " confirm
  case "$confirm" in y|Y|yes|YES) ;; *) echo "Aborted. No changes made."; exit 0 ;; esac
  gh api --method PUT "repos/$REPO/rulesets/$ID" -f "enforcement=$STATE" --jq '{id, name, enforcement}'
  exit 0
fi

shopt -s nullglob
FILES=("$RULESET_DIR"/*.json)
[ ${#FILES[@]} -gt 0 ] || { echo "ERROR: no ruleset files in $RULESET_DIR" >&2; exit 1; }

PLAN=()
for f in "${FILES[@]}"; do
  NAME="$(jq -r '.name' "$f")"
  ID="$(id_for "$NAME")"
  echo
  if [ -n "$ID" ]; then
    echo "[$NAME] found live id=$ID — will UPDATE (PUT)."
    PLAN+=("PUT|repos/$REPO/rulesets/$ID|$f")
  else
    echo "[$NAME] not live — will CREATE (POST)."
    PLAN+=("POST|repos/$REPO/rulesets|$f")
  fi
  jq '{name, enforcement, conditions, bypass_actors, rules: [.rules[].type]}' "$f"
done

if [ "$DRY_RUN" -eq 1 ]; then
  echo; echo "DRY RUN: no changes made."; exit 0
fi

echo
read -r -p "Apply these ${#PLAN[@]} ruleset(s) live to $REPO? [y/N] " confirm
case "$confirm" in y|Y|yes|YES) ;; *) echo "Aborted. No changes made."; exit 0 ;; esac

for entry in "${PLAN[@]}"; do
  IFS='|' read -r METHOD ENDPOINT FILE <<<"$entry"
  gh api --method "$METHOD" "$ENDPOINT" -H "Accept: application/vnd.github+json" \
    --input "$FILE" --jq '{id, name, enforcement, html: ._links.html.href}'
done

echo
echo "Done. Verify on GitHub → Settings → Rules, or re-run with --dry-run to confirm parity."
