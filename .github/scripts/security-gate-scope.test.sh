#!/usr/bin/env bash
# Proves security-gate-scope.sh fires on the changes it exists to review.
#
# Two kinds of case:
#
#   REPLAY  — a real merged PR, replayed by merge-base without checking it out.
#             These are the regression tests that matter: every one of them is a
#             PR the old folder-only filter let through unreviewed.
#   SYNTHETIC — a scratch commit in a temp worktree, for the edge cases history
#             does not happen to contain (docs-only, CancellationToken-only).
#
# Run: bash .github/scripts/security-gate-scope.test.sh

set -uo pipefail
cd "$(dirname "$0")/../.."

SCRIPT=".github/scripts/security-gate-scope.sh"
PASS=0; FAIL=0

decide() { # <base> <head> -> prints "required=.. trigger=.."
  bash "$SCRIPT" --base "$1" --head "$2" 2>/dev/null | tr '\n' ' '
}

expect() { # <label> <expected-required> <expected-trigger> <base> <head>
  local label="$1" want_req="$2" want_trig="$3" base="$4" head="$5"
  local out req trig
  out="$(decide "$base" "$head")"
  req="$(printf '%s' "$out" | grep -oE 'required=[a-z]+' | cut -d= -f2)"
  trig="$(printf '%s' "$out" | grep -oE 'trigger=[a-z]+' | cut -d= -f2)"
  if [ "$req" = "$want_req" ] && [ "$trig" = "$want_trig" ]; then
    printf '  PASS  %-58s required=%s trigger=%s\n' "$label" "$req" "$trig"; PASS=$((PASS+1))
  else
    printf '  FAIL  %-58s got required=%s trigger=%s, want required=%s trigger=%s\n' \
      "$label" "$req" "$trig" "$want_req" "$want_trig"; FAIL=$((FAIL+1))
  fi
}

# A PR's diff is (first parent = main at merge time) .. (second parent = PR head).
# Using the head's OWN parent instead would diff only the branch's last commit,
# which silently under-reports every multi-commit PR.
pr_merge() { git log origin/main --merges --format=%H --grep "Merge pull request #$1 " -n 1; }
pr_base()  { git rev-parse "$(pr_merge "$1")^1" 2>/dev/null; }
pr_head()  { git rev-parse "$(pr_merge "$1")^2" 2>/dev/null; }

echo "REPLAY — PRs the folder-only filter skipped (all must now be reviewed):"
for pr in 203 205 207 208 209 210; do
  head="$(pr_head "$pr")"; base="$(pr_base "$pr")"
  if [ -z "$head" ] || [ -z "$base" ]; then
    printf '  SKIP  #%-4s (merge commit not found in this clone)\n' "$pr"; continue
  fi
  expect "#$pr" true content "$base" "$head"
done

echo
echo "REPLAY — a PR the path filter already caught (must stay a path trigger):"
head="$(pr_head 199)"; base="$(pr_base 199)"
if [ -n "$head" ] && [ -n "$base" ]; then
  expect "#199 (touches gated paths)" true path "$base" "$head"
else
  echo "  SKIP  #199 (merge commit not found in this clone)"
fi

echo
echo "SYNTHETIC — edge cases:"
WORKTREE="$(mktemp -d 2>/dev/null || echo "${TEMP:-/tmp}/sgs-test.$$")"
rm -rf "$WORKTREE"
if git worktree add --detach --quiet "$WORKTREE" HEAD 2>/dev/null; then
  BASE="$(git -C "$WORKTREE" rev-parse HEAD)"

  synth() { # <label> <relpath> <content> <want-required> <want-trigger>
    local label="$1" rel="$2" body="$3" wr="$4" wt="$5"
    mkdir -p "$(dirname "$WORKTREE/$rel")"
    printf '%s\n' "$body" > "$WORKTREE/$rel"
    git -C "$WORKTREE" add -A >/dev/null 2>&1
    git -C "$WORKTREE" -c user.email=t@t -c user.name=t commit -qm "test: $label" >/dev/null 2>&1
    local head; head="$(git -C "$WORKTREE" rev-parse HEAD)"
    expect "$label" "$wr" "$wt" "$BASE" "$head"
    git -C "$WORKTREE" reset -q --hard "$BASE" >/dev/null 2>&1
  }

  synth "docs-only change" \
        "documentation/scratch-test.md" \
        "# A note about passwords and JWT bearer tokens." \
        false none

  synth "src change with only CancellationToken (must NOT fire)" \
        "src/Content/Domain/Domain.AI/ScratchTest.cs" \
        "public static class ScratchTest { public static void Go(System.Threading.CancellationToken cancellationToken) { } }" \
        false none

  synth "src change adding an [Authorize] attribute" \
        "src/Content/Domain/Domain.AI/ScratchTest.cs" \
        "[Authorize] public sealed class ScratchTest { }" \
        true content

  synth "src change touching OwnerId (the recurring defect class)" \
        "src/Content/Domain/Domain.AI/ScratchTest.cs" \
        "public sealed record ScratchTest { public string? OwnerId { get; init; } }" \
        true content

  git worktree remove --force "$WORKTREE" >/dev/null 2>&1
else
  echo "  SKIP  (could not create a scratch worktree)"
fi

echo
echo "SYNTHETIC — failure modes must fail closed:"
if bash "$SCRIPT" 2>/dev/null; then
  echo "  FAIL  missing --base should exit non-zero"; FAIL=$((FAIL+1))
else
  echo "  PASS  missing --base exits non-zero"; PASS=$((PASS+1))
fi
if bash "$SCRIPT" --base "definitely-not-a-ref-$$" 2>/dev/null; then
  echo "  FAIL  unresolvable base should exit non-zero"; FAIL=$((FAIL+1))
else
  echo "  PASS  unresolvable base exits non-zero"; PASS=$((PASS+1))
fi

echo
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
