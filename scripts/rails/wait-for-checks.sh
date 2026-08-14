#!/usr/bin/env bash
#
# wait-for-checks.sh — wait for a PR's CI runs to actually finish, correctly.
#
# `gh pr checks <pr>` only lists runs that currently report a conclusion or an
# in-flight check. The moment a run is re-queued (`gh run rerun --failed`), its row
# disappears from that table entirely rather than showing as pending — a waiter that
# loops "until no row says pending" can therefore declare victory off a table that's
# missing several still-queued gates. This happened for real once: it printed
# "ALL GREEN" while two of six gates had not started.
#
# This polls `gh run list` instead, which does show re-queued runs, then prints the
# checks table once everything has actually settled.
#
# Usage:
#   scripts/rails/wait-for-checks.sh <branch> [pr-number] [poll-seconds]
#
# poll-seconds defaults to 15.

set -euo pipefail

BRANCH="${1:?usage: wait-for-checks.sh <branch> [pr-number] [poll-seconds]}"
PR="${2:-}"
POLL_SECONDS="${3:-15}"

if ! command -v gh >/dev/null 2>&1; then
  echo "wait-for-checks: gh CLI not found." >&2
  exit 1
fi

echo "wait-for-checks: polling runs for branch '$BRANCH' every ${POLL_SECONDS}s..."

while true; do
  PENDING="$(gh run list --branch "$BRANCH" --json status \
    --jq '[.[] | select(.status == "queued" or .status == "in_progress")] | length')"

  if [ "$PENDING" -eq 0 ]; then
    break
  fi

  echo "  $PENDING run(s) still queued/in_progress..."
  sleep "$POLL_SECONDS"
done

echo "wait-for-checks: all runs settled."
echo

if [ -n "$PR" ]; then
  gh pr checks "$PR"
else
  gh run list --branch "$BRANCH" --limit 20
fi
