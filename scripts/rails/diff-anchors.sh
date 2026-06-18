#!/usr/bin/env bash
#
# diff-anchors.sh — emit the deterministic set of changed lines in a PR.
#
# Part of the delivery rails (see .github/RAILS.md). This is the line-anchor
# scaffolding shared by the grader and correctness-review rails: it turns a PR
# diff into a canonical, machine-readable list of changed-line ranges, one per
# contiguous added/modified hunk:
#
#   src/Content/.../Foo.cs:120-145
#   src/Content/.../Foo.cs:201-201
#   src/Content/.../Bar.ts:5-5
#
# Why it exists: an LLM reviewer left to "inspect the diff" produces free-form,
# non-reproducible findings that drift line numbers and wander outside the
# change. Feeding it THIS list as the canonical anchor set forces every finding
# to pin to a real changed line and keeps review scoped to the diff. Same input
# in, same anchors out — the technique is borrowed from deterministic
# code-review scaffolding (e.g. alibaba/open-code-review); only the technique,
# not the tooling.
#
# Output is the anchor list on stdout (sorted, de-duplicated); a one-line count
# summary goes to stderr so stdout stays pure and pipeable. Deleted files are
# excluded (a finding cannot anchor to a line that no longer exists); pure
# deletions inside a surviving file are likewise not emitted — review them via
# the diff itself, anchored to the nearest surviving line.
#
# Requirements: git, awk, sort (all present on ubuntu-latest runners).
#
# Usage:
#   scripts/rails/diff-anchors.sh                       # base = origin/main merge-base
#   scripts/rails/diff-anchors.sh --base origin/develop # explicit base ref
#   BASE_REF=main scripts/rails/diff-anchors.sh         # base ref via env
#   scripts/rails/diff-anchors.sh -- 'src/**'           # restrict to pathspec(s)
#
set -euo pipefail

BASE_REF="${BASE_REF:-main}"
PATHSPECS=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --base)
      BASE_REF="${2:?--base requires a ref}"
      shift 2
      ;;
    --)
      shift
      PATHSPECS=("$@")
      break
      ;;
    *)
      echo "diff-anchors.sh: unknown argument '$1'" >&2
      exit 2
      ;;
  esac
done

# Resolve the merge-base so the diff is the PR's own changes, not everything that
# landed on the base branch since the branch point. Try the ref as given, then
# the origin/-prefixed form (CI checks out with a remote-tracking base).
resolve_base() {
  local ref="$1"
  if git rev-parse --verify --quiet "$ref^{commit}" >/dev/null; then
    git merge-base "$ref" HEAD && return 0
  fi
  if git rev-parse --verify --quiet "origin/$ref^{commit}" >/dev/null; then
    git merge-base "origin/$ref" HEAD && return 0
  fi
  return 1
}

if ! BASE_SHA="$(resolve_base "$BASE_REF")"; then
  echo "diff-anchors.sh: cannot resolve base ref '$BASE_REF' (tried '$BASE_REF' and 'origin/$BASE_REF')." >&2
  exit 1
fi

# --unified=0 makes every hunk header's +start,len describe exactly the
# added/modified new-file lines (no context lines to widen the range).
# --diff-filter=d drops deleted files (their new-side path is /dev/null).
# core.quotePath=false keeps non-ASCII paths literal (default would C-quote and
# octal-escape them, e.g. "b/caf\303\251.txt", breaking the b/ strip below).
anchors="$(
  git -c core.quotePath=false diff --no-color --unified=0 --diff-filter=d "$BASE_SHA" HEAD -- "${PATHSPECS[@]}" \
  | awk '
      # Take everything after the "+++ " marker (substr from col 5), not $2, so a
      # path containing spaces survives; strip the b/ prefix and any trailing tab
      # git appends to delimit a space-bearing name.
      /^\+\+\+ /            { file=substr($0, 5); sub(/^b\//, "", file); sub(/\t.*$/, "", file); next }
      /^@@ /{
        # Hunk header: @@ -oldStart,oldLen +newStart,newLen @@
        if (match($0, /\+[0-9]+(,[0-9]+)?/)) {
          spec = substr($0, RSTART + 1, RLENGTH - 1)   # drop leading "+"
          n = split(spec, a, ",")
          start = a[1] + 0
          len   = (n > 1) ? a[2] + 0 : 1               # omitted length means 1
          if (len > 0 && file != "" && file != "/dev/null")
            print file ":" start "-" (start + len - 1)
        }
      }
    ' \
  | sort -u
)"

count="$(printf '%s' "$anchors" | grep -c . || true)"
echo "diff-anchors.sh: ${count} changed-line range(s) vs ${BASE_SHA}" >&2

# Pure output: nothing but the anchor list (empty when no in-scope changes).
[ -n "$anchors" ] && printf '%s\n' "$anchors"
exit 0
