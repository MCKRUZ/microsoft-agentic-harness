#!/usr/bin/env bash
#
# override-applied-by-owner.sh — is an accepted-risk override label on this PR, AND was it put there
# by the repository owner?
#
# The `accepted-risk:correctness` / `accepted-risk:security` labels clear a blocking finding. Being
# present is not enough: anyone with triage access, or a CI job holding a pull-requests:write token,
# can add a label. Only the latest `labeled` event for it counts, and only when its actor is the
# repository owner. (The agent acts as the owner; .claude/hooks/human-only-guard.ps1 is what stops it
# applying these labels locally.)
#
# Usage: override-applied-by-owner.sh <owner/repo> <pr-number> <label> <owner-login>
# Prints `accepted=true|false` (also to $GITHUB_OUTPUT) and why. Any lookup failure means false.
set -uo pipefail

REPO="$1"; PR="$2"; LABEL="$3"; OWNER="$4"

emit() {
  echo "accepted=$1"
  [ -n "${GITHUB_OUTPUT:-}" ] && echo "accepted=$1" >> "$GITHUB_OUTPUT"
  echo "$2"
  exit 0
}

ON_PR="$(gh api "repos/$REPO/issues/$PR/labels" --paginate --jq '.[].name' 2>/dev/null)" || emit false "Could not read the PR's labels; not honouring an override."
grep -qxF -- "$LABEL" <<<"$ON_PR" || emit false "No '$LABEL' label on the PR."

ACTOR="$(gh api "repos/$REPO/issues/$PR/events" --paginate --jq \
  "map(select(.event == \"labeled\" and .label.name == \"$LABEL\")) | last | .actor.login // empty" 2>/dev/null | tail -n1)" \
  || emit false "Could not read the PR's label history; not honouring '$LABEL'."

if [ -n "$ACTOR" ] && [ "$(printf '%s' "$ACTOR" | tr '[:upper:]' '[:lower:]')" = "$(printf '%s' "$OWNER" | tr '[:upper:]' '[:lower:]')" ]; then
  emit true "'$LABEL' was applied by the repository owner ($ACTOR)."
fi
emit false "::warning::'$LABEL' is on the PR but was applied by '${ACTOR:-unknown}', not the repository owner ($OWNER); it does not override the finding."
