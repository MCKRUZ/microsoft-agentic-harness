#!/usr/bin/env bash
#
# run-gates.sh — run the CI merge gates locally, before pushing.
#
# Part of the delivery rails (see .github/RAILS.md). Every gate here is the SAME
# gate GitHub Actions runs on the PR: same diff base, same changed-line anchors
# (scripts/rails/diff-anchors.sh), same rubrics (.github/*-rubric.md), same
# verdict protocol, same applicability rules, same pass/fail semantics.
#
# WHY IT EXISTS — two reasons, in order:
#
#   1. Cost. The remote reviewers run through the Claude GitHub Action against
#      the SAME Claude subscription this script uses, and they re-run on EVERY push
#      to the PR branch (the workflows trigger on `synchronize`). A fix-then-push
#      rhythm therefore pays for the expensive Opus reviewers two or three times
#      per PR. This script runs the identical reviewers through the LOCAL `claude`
#      CLI, which bills the developer's Claude subscription instead. Clear the
#      gates here, push once, pay for one remote cycle.
#
#   2. Latency. A local BLOCK arrives in minutes without consuming a PR cycle,
#      a CI queue slot, or a reviewer's attention.
#
# WHAT THIS IS NOT: a replacement for the remote gates. It cannot be — it runs on
# the developer's machine, on their working tree, with their credentials, and the
# remote gates re-derive their own verdict server-side regardless of what happened
# here. The remote gates remain the enforcement boundary; this script is the fast,
# cheap pre-flight that makes the remote run a formality instead of a discovery
# process. Do not disable the workflows on the strength of this script.
#
# A full, default-base, all-gates-passed run through THIS script DOES get verified,
# though: it writes its own "run-gates" receipt (see the end of this file), which
# .claude/hooks/review-gate.ps1 requires before allowing a push through Claude Code.
# That closes the specific gap where a fix-then-push rhythm never bothered to run
# this pre-flight at all and paid for a fresh remote correctness-review / grader
# cycle on every small fix.
#
# HONEST DIFFERENCES FROM CI (each one deliberate, none of them silent):
#   * Billing: your local `claude` CLI session; CI uses CLAUDE_CODE_OAUTH_TOKEN. Same pot.
#   * No PR comment is posted — findings print to your terminal. There is no PR.
#   * No turn ceiling. The remote gates pass --max-turns to bound API spend; the
#     local CLI exposes no equivalent flag, so a local review is unbounded in
#     turns. The provisional-verdict fail-safe below still applies, so a review
#     that dies mid-flight still reports BLOCK rather than silently passing.
#   * Label overrides (`accepted-risk:correctness` / `accepted-risk:security`) have
#     no local equivalent, so --accept-risk exists to mirror them for pre-flight
#     purposes only. It records nothing and overrides nothing on the real PR.
#   * The base is your local ref (default `main`), not github.base_ref.
#
# THE PROVISIONAL-VERDICT FAIL-SAFE, preserved verbatim from CI: the reviewer is
# told to write "BLOCK / PROVISIONAL" as its FIRST action, then overwrite it with
# a real verdict at the end. A reviewer that crashes, hangs, or is interrupted
# therefore leaves a BLOCK behind — the correct answer when no review completed.
# An unreplaced provisional block is reported as an INFRASTRUCTURE fault, never as
# a finding about the code, and --accept-risk deliberately does NOT override it:
# accepting a known defect and accepting not having looked are different decisions.
#
# Verdict files are written under a temp dir OUTSIDE the working tree, for the
# same reason CI writes them under RUNNER_TEMP: a verdict file committed into the
# repo must never be able to satisfy a gate.
#
# Usage:
#   scripts/rails/run-gates.sh                  # every applicable gate
#   scripts/rails/run-gates.sh --fast           # compile/test gates only, no AI reviewers
#   scripts/rails/run-gates.sh --ai-only        # AI reviewers only
#   scripts/rails/run-gates.sh --correctness    # one named gate (repeatable)
#   scripts/rails/run-gates.sh --base develop   # diff against a different base
#   scripts/rails/run-gates.sh --list           # show which gates apply and why
#
# Exit code: 0 if every selected gate passed or was not applicable; 1 otherwise.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT" || exit 1

BASE_REF=""
BASE_EXPLICIT=false
ACCEPT_RISK=""
COVERAGE=false
LIST_ONLY=false
SELECTED=()

usage() {
  sed -n '3,60p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 0
}

while [ $# -gt 0 ]; do
  case "$1" in
    --all)          SELECTED=(build test owasp docs-links grader correctness security docs-drift) ;;
    --fast)         SELECTED=(build test owasp docs-links) ;;
    --ai-only)      SELECTED=(grader correctness security docs-drift) ;;
    --docs-drift)   SELECTED+=(docs-drift) ;;
    --build)        SELECTED+=(build) ;;
    --test)         SELECTED+=(test) ;;
    --owasp)        SELECTED+=(owasp) ;;
    --docs-links)   SELECTED+=(docs-links) ;;
    --grader)       SELECTED+=(grader) ;;
    --correctness)  SELECTED+=(correctness) ;;
    --security)     SELECTED+=(security) ;;
    --coverage)     COVERAGE=true ;;
    --list)         LIST_ONLY=true ;;
    --base)         shift; BASE_REF="${1:-main}"; BASE_EXPLICIT=true ;;
    --accept-risk)  shift; ACCEPT_RISK="${ACCEPT_RISK} ${1:-}" ;;
    -h|--help)      usage ;;
    *) echo "run-gates: unknown option '$1' (try --help)" >&2; exit 2 ;;
  esac
  shift
done

[ ${#SELECTED[@]} -eq 0 ] && SELECTED=(build test owasp docs-links grader correctness security docs-drift)

# When --base was not given, resolve the default EXACTLY the way review-scope.ps1's
# Resolve-ReviewBase does (prefer origin/main, fall back to main) — not a bare literal
# "main". These must agree: review-gate.ps1 requires the run-gates receipt against the
# base IT resolves, and save-review-receipt.ps1 (called at the end of this script)
# resolves its own base the same way independently of whatever $BASE_REF ends up being
# here. If this script defaulted to a literal "main" while origin/main has diverged, the
# gates below would review a different diff than the one the receipt actually attests
# to. Only an explicit --base skips this — that's a deliberate exploratory override, and
# it already forfeits the receipt (see the guard near the end of this file).
if ! $BASE_EXPLICIT; then
  BASE_REF="main"
  if git rev-parse --verify --quiet origin/main >/dev/null 2>&1; then
    BASE_REF="origin/main"
  fi
fi

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------
if ! git rev-parse --verify "$BASE_REF" >/dev/null 2>&1; then
  echo "run-gates: base ref '$BASE_REF' does not resolve. Fetch it or pass --base." >&2
  exit 2
fi

NEEDS_CLAUDE=false
for g in "${SELECTED[@]}"; do
  case "$g" in grader|correctness|security|docs-drift) NEEDS_CLAUDE=true ;; esac
done
if $NEEDS_CLAUDE && ! command -v claude >/dev/null 2>&1; then
  echo "run-gates: the 'claude' CLI is not on PATH — the AI gates cannot run." >&2
  echo "           Use --fast to run only the compile/test gates." >&2
  exit 2
fi

# No predictable-path fallback: this directory holds AI-reviewer verdict files and (as
# of the run-gates receipt) the push-gate receipt writer's output, so a guessable path
# here is pre-creatable by anything else on the same machine, defeating both. Fail hard
# instead of degrading to a guessable location.
TMPDIR_GATES="$(mktemp -d)" || { echo "run-gates: mktemp failed — cannot create a private scratch directory. Aborting." >&2; exit 1; }
trap 'rm -rf "$TMPDIR_GATES"' EXIT

# The reviewer runs as a separate `claude` process. On Windows under Git Bash that
# process is native Win32 and resolves a POSIX path like /tmp/tmp.AbCd against its
# own root, NOT against the MSYS mount — so a verdict written to the path we handed
# it lands somewhere we never look, and the gate reports "no verdict" (an
# infrastructure fault) even though the review completed and passed. Hand the
# reviewer a native path; keep the POSIX one for our own file tests.
native_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}
TMPDIR_GATES_NATIVE="$(native_path "$TMPDIR_GATES")"

# ---------------------------------------------------------------------------
# Applicability — mirrors the `detect` step in each workflow exactly.
# ---------------------------------------------------------------------------
ANCHORS_FILE="${TMPDIR_GATES}/correctness-anchors.txt"
if ! scripts/rails/diff-anchors.sh --base "$BASE_REF" -- 'src/' > "$ANCHORS_FILE" 2>/dev/null; then
  echo "run-gates: diff-anchors.sh failed — cannot compute the changed-line scope. Failing closed." >&2
  exit 1
fi
ANCHORS_FILE_NATIVE="$(native_path "$ANCHORS_FILE")"
SRC_CHANGED=false
[ -s "$ANCHORS_FILE" ] && SRC_CHANGED=true

CHANGED_FILES="$(git diff --name-only "${BASE_REF}...HEAD" 2>/dev/null || true)"

# The security gate's applicability is decided by ONE script, shared with
# .github/workflows/security-review.yml, so the local pre-flight and the remote
# required check can never disagree. It selects on what the diff CONTAINS, not on
# folder names alone — the folder-only filter this replaced skipped six consecutive
# PRs that changed authorization, tenant scoping and capability-envelope code.
SECURITY_SCOPE_FILE="${TMPDIR_GATES}/security-scope.txt"
export SECURITY_SCOPE_FILE
SECURITY_SCOPE_OUT="${TMPDIR_GATES}/security-scope-decision.txt"
if ! bash .github/scripts/security-gate-scope.sh --base "$BASE_REF" > "$SECURITY_SCOPE_OUT"; then
  echo "run-gates: security-gate-scope.sh could not decide — failing closed (its error is above)." >&2
  exit 1
fi
SECURITY_GATED=false
[ "$(grep '^required=' "$SECURITY_SCOPE_OUT" | cut -d= -f2)" = "true" ] && SECURITY_GATED=true
SECURITY_REASON="$(grep '^reason=' "$SECURITY_SCOPE_OUT" | cut -d= -f2-)"
SECURITY_SCOPE_FILE_NATIVE="$(native_path "$SECURITY_SCOPE_FILE")"

DOCS_CHANGED=false
echo "$CHANGED_FILES" | grep -Eq '^documentation/' && DOCS_CHANGED=true

applies() {
  case "$1" in
    correctness) $SRC_CHANGED ;;
    security)    $SECURITY_GATED ;;
    grader)      $SRC_CHANGED ;;
    docs-links)  $DOCS_CHANGED ;;
    docs-drift)  $SRC_CHANGED || $DOCS_CHANGED ;;
    *)           true ;;
  esac
}

reason_for() {
  case "$1" in
    correctness) $SRC_CHANGED && echo "$(grep -c . "$ANCHORS_FILE") changed-line range(s) under src/" || echo "no source under src/ changed" ;;
    security)    echo "$SECURITY_REASON" ;;
    grader)      $SRC_CHANGED && echo "source under src/ changed" || echo "no source under src/ changed" ;;
    docs-links)  $DOCS_CHANGED && echo "documentation/ changed" || echo "documentation/ unchanged" ;;
    docs-drift)  ($SRC_CHANGED || $DOCS_CHANGED) && echo "code or docs changed — docs may be stale" || echo "nothing that could stale the docs changed" ;;
    *)           echo "always runs" ;;
  esac
}

if $LIST_ONLY; then
  printf 'Base: %s   HEAD: %s\n\n' "$BASE_REF" "$(git rev-parse --short HEAD)"
  printf '%-14s %-14s %s\n' "GATE" "APPLIES" "WHY"
  for g in "${SELECTED[@]}"; do
    applies "$g" && a="yes" || a="no (skip)"
    printf '%-14s %-14s %s\n' "$g" "$a" "$(reason_for "$g")"
  done
  exit 0
fi

# ---------------------------------------------------------------------------
# Gate runners
# ---------------------------------------------------------------------------
FAILED=()
PASSED=()
SKIPPED=()

banner() { printf '\n\033[1m=== %s ===\033[0m\n' "$1"; }

run_dotnet_gate() {
  local name="$1"; shift
  banner "$name"
  if "$@"; then PASSED+=("$name"); return 0; else FAILED+=("$name"); return 1; fi
}

# Runs one AI reviewer end to end: provisional verdict, review against the
# rubric, then enforce the replaced verdict with the same first-line prefix match
# CI uses (a model that buries the token in prose must not pass).
run_ai_gate() {
  local name="$1" token="$2" model="$3" rubric="$4" scope_note="$5"
  local verdict_file="${TMPDIR_GATES}/${name}-verdict.txt"
  local verdict_file_native
  verdict_file_native="$(native_path "$verdict_file")"

  banner "$name (model: $model — billed to your Claude subscription)"
  rm -f "$verdict_file"

  local prompt
  prompt=$(cat <<PROMPT
You are running as the ${name} gate for a local pre-push check.

STEP 1 — DO THIS FIRST, BEFORE READING ANYTHING ELSE.
Write this exact two-line content to ${verdict_file_native}:

${token}: BLOCK
reason: PROVISIONAL - the review did not run to completion

This is a fail-safe. If you error out or are interrupted later, that provisional
block is what the gate sees, and it is the correct answer. Do not skip it and do
not leave it until the end.

STEP 2 — Review.
Read the rubric at ${rubric} and follow it exactly.
${scope_note}
The diff base is ${BASE_REF}. Prefer a single \`git diff\` over many per-file
reads, and do not re-read a file you have already seen.

STEP 3 — Replace the verdict, then print your findings.
Overwrite ${verdict_file_native} with your real verdict. The first line must be EXACTLY
"${token}: PASS" or "${token}: BLOCK". On BLOCK, add a second line
"reason: <one-line summary>" — never containing the word PROVISIONAL, which is
reserved for the step-1 fail-safe.
Then print your findings to stdout. There is no PR to comment on.
PROMPT
)

  claude -p "$prompt" \
    --model "$model" \
    --allowedTools "Bash,Read,Grep,Glob,Write" \
    2>/dev/null

  if [ ! -f "$verdict_file" ]; then
    echo "run-gates: ${name} wrote no verdict at all — it failed before its first action."
    echo "           This is an INFRASTRUCTURE fault, not a finding about your code."
    FAILED+=("$name (no verdict — infrastructure)")
    return 1
  fi

  local first reason
  first="$(head -n1 "$verdict_file" || true)"
  reason="$(sed -n '2p' "$verdict_file" || true)"

  case "$first" in
    "${token}: BLOCK"*)
      case "$reason" in
        *PROVISIONAL*)
          echo "run-gates: ${name} did not run to completion — its provisional block was never replaced."
          echo "           INFRASTRUCTURE fault, not a finding. --accept-risk does NOT override this:"
          echo "           that flag accepts a known defect, not an absent review."
          FAILED+=("$name (incomplete — infrastructure)")
          return 1 ;;
      esac
      if [[ " $ACCEPT_RISK " == *" ${name} "* ]]; then
        echo "run-gates: ${name} found a defect, overridden locally by --accept-risk ${name}."
        echo "           Reason: ${reason}"
        echo "           NOTE: this override is local only. The PR still needs the real"
        echo "                 accepted-risk:${name} label, which is audited in the timeline."
        PASSED+=("$name (risk accepted locally)")
        return 0
      fi
      echo "run-gates: ${name} BLOCKED — ${reason}"
      FAILED+=("$name")
      return 1 ;;
    "${token}: PASS"*)
      echo "run-gates: ${name} passed."
      PASSED+=("$name")
      return 0 ;;
    *)
      echo "run-gates: unrecognized ${name} verdict ('${first}'). Failing closed."
      FAILED+=("$name (unrecognized verdict)")
      return 1 ;;
  esac
}

# ---------------------------------------------------------------------------
# Execute
# ---------------------------------------------------------------------------
printf 'Running gates against base \033[1m%s\033[0m (HEAD %s)\n' "$BASE_REF" "$(git rev-parse --short HEAD)"

for gate in "${SELECTED[@]}"; do
  if ! applies "$gate"; then
    SKIPPED+=("$gate — $(reason_for "$gate")")
    continue
  fi

  case "$gate" in
    build)
      run_dotnet_gate "build" dotnet build src/AgenticHarness.slnx --configuration Release
      ;;
    test)
      if $COVERAGE; then
        run_dotnet_gate "test" dotnet test src/AgenticHarness.slnx --no-build --configuration Release \
          --collect:"XPlat Code Coverage" --results-directory coverage
      else
        run_dotnet_gate "test" dotnet test src/AgenticHarness.slnx --no-build --configuration Release
      fi
      ;;
    owasp)
      run_dotnet_gate "owasp" dotnet test \
        src/Content/Tests/Application.AI.Common.Tests/Application.AI.Common.Tests.csproj \
        --no-build --configuration Release --filter "Category=OwaspAgentic"
      ;;
    docs-links)
      run_dotnet_gate "docs-links" node .github/scripts/check-docs-links.mjs
      ;;
    grader)
      run_ai_gate "grader" "GRADE" "sonnet" ".github/grader-rubric.md" \
        "The changed-line anchor set is at ${ANCHORS_FILE_NATIVE} — grade ONLY those lines and the code they directly touch, and anchor every finding to a line from that file."
      ;;
    correctness)
      run_ai_gate "correctness" "CORRECTNESS_VERDICT" "opus" ".github/correctness-review-rubric.md" \
        "The changed-line anchor set is at ${ANCHORS_FILE_NATIVE} — review ONLY those lines and the code they directly touch, and anchor every finding to a line from that file."
      ;;
    docs-drift)
      # Advisory, exactly like the workflow (which is continue-on-error and never
      # blocks a merge). The CI version opens a doc-sync PR; locally there is
      # nothing to open a PR against, so it reports the drift and you fix it in
      # place — which is the better outcome anyway, since the docs then ship in
      # the same PR as the change that staled them.
      banner "docs-drift (model: sonnet — advisory, billed to your Claude subscription)"
      claude -p "You are the documentation-drift checker for a local pre-push check.
Read the rubric at .github/docs-drift-rubric.md and follow it EXACTLY.
The change to assess is \`git diff ${BASE_REF}...HEAD\`.
Decide whether any documentation in documentation/** (the GitHub Pages sites) is
now stale or missing coverage because of this change, per the rubric's doc map.
Do NOT create a branch, do NOT commit, and do NOT open a pull request — this is a
local pre-push check with nothing to push to. Instead print, per file, exactly
what drifted and the minimal edit that would fix it, so the author can make the
change in this same branch. If you find no drift, say 'no drift' and stop." \
        --model sonnet \
        --allowedTools "Bash,Read,Grep,Glob" \
        2>/dev/null
      echo "run-gates: docs-drift is advisory — it never fails the run. Act on anything above."
      PASSED+=("docs-drift (advisory)")
      ;;
    security)
      run_ai_gate "security" "SECURITY_VERDICT" "opus" ".github/security-review-rubric.md" \
        "Review the changed files listed by \`git diff --name-only ${BASE_REF}...HEAD\`. The file ${SECURITY_SCOPE_FILE_NATIVE} lists the files whose own changed lines carry a security signal — start there, then widen only if a finding leads you out of that set."
      ;;
  esac
done

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
banner "SUMMARY"
for s in "${SKIPPED[@]:-}"; do [ -n "$s" ] && printf '  \033[2mskip\033[0m  %s\n' "$s"; done
for p in "${PASSED[@]:-}";  do [ -n "$p" ] && printf '  \033[32mpass\033[0m  %s\n' "$p"; done
for f in "${FAILED[@]:-}";  do [ -n "$f" ] && printf '  \033[31mFAIL\033[0m  %s\n' "$f"; done

if [ ${#FAILED[@]} -gt 0 ]; then
  printf '\n\033[31m%d gate(s) failed.\033[0m Fix them before pushing — the same gates run on the PR.\n' "${#FAILED[@]}"
  exit 1
fi

printf '\n\033[32mAll selected gates passed.\033[0m The remote run should be a formality.\n'

# ---------------------------------------------------------------------------
# Push-gate receipt — ONLY for a full, default-base run.
#
# review-gate.ps1 (the local push gate) requires a "run-gates" receipt in addition to
# the /code-review and /simplify ones. That receipt is written HERE, automatically, on
# a real passing run — never by hand — so its existence actually proves the local gates
# ran, rather than being another claim an agent could type without running anything.
#
# Deliberately narrow: a partial run (a single named gate, --fast, --ai-only), an
# explicit --base override, a run where --accept-risk overrode a BLOCK, or a run against
# a dirty src/ tree does not represent "the same clean checks the push gate cares about",
# so none of those earn a receipt. Run with no flags (equivalent to --all), no
# --accept-risk, with src/ clean, to satisfy the push gate. An explicit --base always
# forfeits the receipt, even --base origin/main matching the auto-resolved default
# exactly — a plain no-flags run already resolves to the correct base, so there's no
# legitimate reason to pass --base and still expect one.
# ---------------------------------------------------------------------------
FULL_SET_SORTED="$(printf '%s\n' build test owasp docs-links grader correctness security docs-drift | sort | tr '\n' ' ')"
SELECTED_SORTED="$(printf '%s\n' "${SELECTED[@]}" | sort -u | tr '\n' ' ')"
NO_RECEIPT_REASON=""
if $BASE_EXPLICIT; then
  NO_RECEIPT_REASON="--base was explicitly given ('${BASE_REF}') — run with no --base to use the auto-resolved default and earn a receipt"
elif [ "$SELECTED_SORTED" != "$FULL_SET_SORTED" ]; then
  NO_RECEIPT_REASON="partial gate selection (${SELECTED[*]})"
elif [ -n "${ACCEPT_RISK// /}" ]; then
  # A gate that BLOCKed and was overridden locally still lands in PASSED (see
  # run_ai_gate's "risk accepted locally" branch), so FAILED stays empty and this
  # function would otherwise print a receipt claiming a clean pass over a real BLOCK.
  NO_RECEIPT_REASON="--accept-risk was used (${ACCEPT_RISK}) — a risk-accepted run does not attest to a clean pass"
elif ! DIRTY_SRC="$(git status --porcelain -- src 2>&1)"; then
  # Fail CLOSED on git itself failing (index lock, corrupt worktree, etc.) — the earlier
  # version only checked stdout, so a failing `git status` looked identical to a clean
  # tree and would have written a receipt with no idea whether src/ was actually clean.
  NO_RECEIPT_REASON="git status failed, so src/ cleanliness could not be confirmed: ${DIRTY_SRC}"
elif [ -n "$DIRTY_SRC" ]; then
  # The receipt's fingerprint covers the COMMITTED diff (review-scope.ps1), but the
  # gates above just ran against the working tree. With src/ dirty those can disagree,
  # so a receipt here would attest to code that was never actually tested.
  NO_RECEIPT_REASON="src/ has uncommitted changes — the gates ran against the working tree, not the committed diff the receipt would attest to"
fi

if [ -z "$NO_RECEIPT_REASON" ]; then
  RECEIPT_OUT="${TMPDIR_GATES}/run-gates-receipt-output.txt"
  RECEIPT_SUMMARY="run-gates.sh (all gates) passed at $(git rev-parse --short HEAD) against base ${BASE_REF}: $(IFS=,; echo "${PASSED[*]:-none}")"
  if command -v pwsh >/dev/null 2>&1; then
    if printf '%s\n' "$RECEIPT_SUMMARY" | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind run-gates >"$RECEIPT_OUT" 2>&1; then
      cat "$RECEIPT_OUT"
    else
      echo "run-gates: WARNING — could not record the run-gates receipt; the push gate will still ask for it:" >&2
      cat "$RECEIPT_OUT" >&2
    fi
  else
    echo "run-gates: WARNING — pwsh not on PATH, could not record the run-gates receipt." >&2
    echo "           The push gate will still require it. Install PowerShell 7+ or run" >&2
    echo "           save-review-receipt.ps1 manually via a pwsh you do have on this machine." >&2
  fi
else
  echo "run-gates: no run-gates receipt recorded — ${NO_RECEIPT_REASON}."
  echo "           Run with no flags, no --base, no --accept-risk, and src/ clean, to satisfy the push gate."
fi

printf '\nReminder: this does not replace the PR gates — the review-gate hook also still\n'
printf 'needs its own /code-review and /simplify receipts.\n'
exit 0
