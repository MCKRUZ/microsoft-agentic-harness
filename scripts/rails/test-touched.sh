#!/usr/bin/env bash
#
# test-touched.sh — run only the test project(s) that own the code changed on this
# branch, instead of the full 20+ project solution, for fast iteration.
#
# Why this exists: a session on 2026-08-13 ran the full-solution test suite three
# times while iterating on a change that only ever touched four projects. Each full
# run also drags in unrelated, environment-dependent failures (Docker not running
# locally) that have nothing to do with the change and have to be manually triaged
# away every time. This script scopes iteration runs to what could plausibly have
# broken.
#
# THIS DOES NOT REPLACE THE FULL-SOLUTION RUN. A change can break a project it never
# touches directly (a shared interface, a behavior contract). Run
# `dotnet test src/AgenticHarness.slnx` once, for real, before every push — this
# script is for the loop before that, not instead of it.
#
# Usage:
#   scripts/rails/test-touched.sh [base-ref]
#
# base-ref defaults to origin/main. Any extra arguments after base-ref are passed
# through verbatim to each `dotnet test` invocation (e.g. --filter, -v).

set -euo pipefail

BASE_REF="${1:-origin/main}"
shift || true
EXTRA_ARGS=("$@")

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

# Full (non-shallow) fetch of the base so the merge-base actually exists — mirrors
# the same guard correctness-review.yml uses, for the same reason.
if ! git rev-parse --verify "$BASE_REF" >/dev/null 2>&1; then
  echo "test-touched: base ref '$BASE_REF' not found locally. Try 'git fetch origin main' first." >&2
  exit 1
fi

CHANGED_FILES="$(git diff --name-only --diff-filter=ACMR "$BASE_REF"...HEAD -- 'src/Content/**/*.cs' || true)"

if [ -z "$CHANGED_FILES" ]; then
  echo "test-touched: no changed .cs files under src/Content vs $BASE_REF — nothing to run."
  exit 0
fi

# For each changed file, walk up to its nearest .csproj and record the project name.
# A project whose name already ends in .Tests is its own target; otherwise resolve
# the sibling <Name>.Tests project under src/Content/Tests/.
declare -A TEST_PROJECT_PATHS=()
declare -A SOURCE_PROJECTS_SEEN=()
declare -a UNRESOLVED=()

while IFS= read -r file; do
  [ -z "$file" ] && continue
  dir="$(dirname "$REPO_ROOT/$file")"
  csproj=""
  while [ "$dir" != "$REPO_ROOT" ] && [ "$dir" != "/" ]; do
    match="$(find "$dir" -maxdepth 1 -name '*.csproj' 2>/dev/null | head -n1)"
    if [ -n "$match" ]; then
      csproj="$match"
      break
    fi
    dir="$(dirname "$dir")"
  done

  if [ -z "$csproj" ]; then
    UNRESOLVED+=("$file")
    continue
  fi

  project_name="$(basename "$csproj" .csproj)"
  [ -n "${SOURCE_PROJECTS_SEEN[$project_name]:-}" ] && continue
  SOURCE_PROJECTS_SEEN["$project_name"]=1

  if [[ "$project_name" == *.Tests ]]; then
    TEST_PROJECT_PATHS["$project_name"]="$csproj"
    continue
  fi

  test_project_name="${project_name}.Tests"
  test_project_dir="$REPO_ROOT/src/Content/Tests/$test_project_name"
  test_csproj=""
  # A plain `find` on a path that doesn't exist yet exits non-zero (unlike finding
  # zero matches inside a real directory, which exits 0) — under `set -e`/pipefail
  # that silently kills the whole script the first time a project has no test
  # sibling (e.g. Presentation.ConsoleUI). Guard with -d first.
  if [ -d "$test_project_dir" ]; then
    test_csproj="$(find "$test_project_dir" -maxdepth 1 -name "$test_project_name.csproj" | head -n1)"
  fi
  if [ -n "$test_csproj" ]; then
    TEST_PROJECT_PATHS["$test_project_name"]="$test_csproj"
  else
    echo "test-touched: no sibling test project found for '$project_name' (changed: $file) — not included in this run." >&2
  fi
done <<< "$CHANGED_FILES"

if [ ${#UNRESOLVED[@]} -gt 0 ]; then
  echo "test-touched: could not resolve a project for: ${UNRESOLVED[*]}" >&2
fi

if [ ${#TEST_PROJECT_PATHS[@]} -eq 0 ]; then
  echo "test-touched: changed files resolved to zero test projects. Run the full solution instead."
  exit 0
fi

echo "test-touched: running ${#TEST_PROJECT_PATHS[@]} test project(s) for the diff vs $BASE_REF:"
for name in "${!TEST_PROJECT_PATHS[@]}"; do
  echo "  - $name"
done
echo

STATUS=0
for name in "${!TEST_PROJECT_PATHS[@]}"; do
  echo "=== $name ==="
  if ! dotnet test "${TEST_PROJECT_PATHS[$name]}" "${EXTRA_ARGS[@]}"; then
    STATUS=1
  fi
  echo
done

echo "test-touched: done. This is a SCOPED run — run 'dotnet test src/AgenticHarness.slnx' before push."
exit "$STATUS"
