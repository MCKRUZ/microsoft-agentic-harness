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

scope_of() { # <base> <head> -> the scope file's contents
  local out
  out="$(mktemp 2>/dev/null || echo "${TEMP:-/tmp}/sgs-scope.$$")"
  SECURITY_SCOPE_FILE="$out" bash "$SCRIPT" --base "$1" --head "$2" >/dev/null 2>&1
  cat "$out"; rm -f "$out"
}

expect_scope_contains() { # <label> <base> <head> <path-substring>
  local label="$1" base="$2" head="$3" want="$4"
  if scope_of "$base" "$head" | grep -qF "$want"; then
    printf '  PASS  %-58s scope includes %s\n' "$label" "$want"; PASS=$((PASS+1))
  else
    printf '  FAIL  %-58s scope MISSING %s\n' "$label" "$want"; FAIL=$((FAIL+1))
  fi
}

expect_scope_nonempty() { # <label> <base> <head>
  local label="$1" base="$2" head="$3"
  if [ -n "$(scope_of "$base" "$head")" ]; then
    printf '  PASS  %-58s scope is non-empty\n' "$label"; PASS=$((PASS+1))
  else
    printf '  FAIL  %-58s scope is EMPTY while required=true\n' "$label"; FAIL=$((FAIL+1))
  fi
}

expect() { # <label> <expected-required> <expected-trigger> <base> <head>
  local label="$1" want_req="$2" want_trig="$3" base="$4" head="$5"
  local out req trig
  out="$(decide "$base" "$head")"
  req="$(printf '%s' "$out" | grep -oE 'required=[a-z]+' | cut -d= -f2)"
  # [a-z+] — the combined trigger is literally "path+content"; a [a-z]+ class
  # truncates it to "path" and the assertion silently compares the wrong string.
  trig="$(printf '%s' "$out" | grep -oE 'trigger=[a-z+]+' | cut -d= -f2)"
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
# #199 changed gated paths AND security-relevant code. Under the old exclusive
# branching it reported `path` and its src/ files never reached the scope file;
# reporting `path+content` is the fix, not a regression.
echo "REPLAY — a PR the path filter already caught (must keep firing):"
head="$(pr_head 199)"; base="$(pr_base 199)"
if [ -n "$head" ] && [ -n "$base" ]; then
  expect "#199 (gated paths + security-relevant code)" true "path+content" "$base" "$head"
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

  synth "TypeScript touching credentials (frontend is scanned too)" \
        "src/Content/Presentation/agent-hub-ui/src/scratchTest.ts" \
        "export const authHeader = (apiKey: string) => ({ Authorization: \`Bearer \${apiKey}\` });" \
        true content

  # REGRESSION — the HIGH the security reviewer found in the first draft of this
  # script. A gated-path touch used to SUPPRESS the content scan, so a PR pairing a
  # one-line .github/ edit with an owner-check change handed the reviewer a scope
  # file naming only the .github file. Both signals must survive, and the scope file
  # must be the union.
  echo
  echo "REGRESSION — a gated-path touch must not suppress the content signal:"
  mkdir -p "$WORKTREE/src/Content/Domain/Domain.AI"
  printf '%s\n' "public sealed record ScratchTest { public string? OwnerId { get; init; } }" \
    > "$WORKTREE/src/Content/Domain/Domain.AI/ScratchTest.cs"
  printf '%s\n' "# scratch" >> "$WORKTREE/.github/CODEOWNERS"
  git -C "$WORKTREE" add -A >/dev/null 2>&1
  git -C "$WORKTREE" -c user.email=t@t -c user.name=t commit -qm "test: path plus content" >/dev/null 2>&1
  MIXED="$(git -C "$WORKTREE" rev-parse HEAD)"
  expect "path + content together" true "path+content" "$BASE" "$MIXED"
  expect_scope_contains "  scope keeps the gated path" "$BASE" "$MIXED" ".github/CODEOWNERS"
  expect_scope_contains "  scope keeps the src file the reviewer must see" "$BASE" "$MIXED" "ScratchTest.cs"
  git -C "$WORKTREE" reset -q --hard "$BASE" >/dev/null 2>&1

  # The rails scripts ARE the gates — a change to one must be reviewed even though
  # it carries no marker of its own (SECURITY_GATED is not a marker; the list is
  # case-sensitive on purpose). This was the reviewer's example of a file in the
  # diff but absent from the scope file.
  printf '%s\n' "# scratch" >> "$WORKTREE/scripts/rails/run-gates.sh"
  git -C "$WORKTREE" add -A >/dev/null 2>&1
  git -C "$WORKTREE" -c user.email=t@t -c user.name=t commit -qm "test: rails script" >/dev/null 2>&1
  RAILS="$(git -C "$WORKTREE" rev-parse HEAD)"
  expect "a change to run-gates.sh is itself gated" true path "$BASE" "$RAILS"
  expect_scope_contains "  scope names the rails script" "$BASE" "$RAILS" "scripts/rails/run-gates.sh"
  git -C "$WORKTREE" reset -q --hard "$BASE" >/dev/null 2>&1

  # A scope file must never be empty while the gate says review is required.
  printf '%s\n' "# just a workflow comment" >> "$WORKTREE/.github/CODEOWNERS"
  git -C "$WORKTREE" add -A >/dev/null 2>&1
  git -C "$WORKTREE" -c user.email=t@t -c user.name=t commit -qm "test: path only" >/dev/null 2>&1
  expect_scope_nonempty "path-only change still names files" "$BASE" "$(git -C "$WORKTREE" rev-parse HEAD)"
  git -C "$WORKTREE" reset -q --hard "$BASE" >/dev/null 2>&1

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
