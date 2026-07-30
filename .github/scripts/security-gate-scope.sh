#!/usr/bin/env bash
# Decides whether the security reviewer must run for a diff, and what it should look at.
#
# WHY THIS FILE EXISTS
# --------------------
# The gate used to select purely by FOLDER NAME (/Auth/, /Identity/, /Security/,
# /Migrations/, .github/, infra/). That filter skipped six consecutive PRs — #203,
# #205, #207, #208, #209, #210 — every one of which changed authorization,
# multi-tenant scoping, or capability-envelope code. It has also been observed
# missing two HIGH findings in a single day. A gate that does not fire on the
# changes it exists to review is not a gate.
#
# Folder names describe where code lives, not what it does. This repo keeps its
# security-critical logic in Controllers/, Runs/, Planner/ and Interfaces/ — so the
# decision is made on what the diff SAYS, with the old path list kept as a
# supplement (a migration or a workflow file is worth reviewing whatever it says).
#
# SINGLE AUTHORITY
# ----------------
# Both callers — .github/workflows/security-review.yml (remote, blocking) and
# scripts/rails/run-gates.sh (local pre-flight) — call this script. They previously
# carried duplicate copies of the regex under a "keep in sync" comment, which is the
# drift trap this repo has closed elsewhere (ApproverClaimTypes, EscalationStepOutput,
# ConditionExpressionRules). One definition, two readers.
#
# USAGE
#   security-gate-scope.sh --base <ref> [--head <ref>] [--format github|human]
#
# `--head` defaults to HEAD. It exists so the decision can be replayed against any
# historical commit without checking it out — which is how the test harness proves
# the gate fires on the PRs it used to miss.
#
# OUTPUT (key=value lines; `github` format also appends them to $GITHUB_OUTPUT)
#   required=true|false   whether the reviewer must run
#   trigger=path|content|path+content|none
#   reason=<one line, human-readable>
#   signals=<comma-separated markers matched; empty only when nothing matched>
#
# The two signals are additive: a diff can trigger on both, and the scope file is
# then the UNION. Never make one branch suppress the other — see the comment on the
# TRIGGER block for the hole that created.
#
# The matched files are written to the path given by SECURITY_SCOPE_FILE when set,
# so the caller can point the reviewer at them instead of the whole diff.
#
# EXIT CODES
#   0  decision made (read `required`)
#   2  usage error or the diff could not be computed — callers must FAIL CLOSED.

set -euo pipefail

BASE_REF=""
HEAD_REF="HEAD"
FORMAT="human"

while [ $# -gt 0 ]; do
  case "$1" in
    --base)   BASE_REF="${2:-}"; shift 2 ;;
    --head)   HEAD_REF="${2:-HEAD}"; shift 2 ;;
    --format) FORMAT="${2:-human}"; shift 2 ;;
    -h|--help) sed -n '1,40p' "$0"; exit 0 ;;
    *) echo "security-gate-scope: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

[ -n "$BASE_REF" ] || { echo "security-gate-scope: --base <ref> is required" >&2; exit 2; }

# Three-dot semantics: compare against the merge-base, so commits that landed on the
# base branch after this one forked are not attributed to this diff.
MERGE_BASE="$(git merge-base "$BASE_REF" "$HEAD_REF" 2>/dev/null || true)"
[ -n "$MERGE_BASE" ] || { echo "security-gate-scope: no merge-base between '$BASE_REF' and '$HEAD_REF'" >&2; exit 2; }

CHANGED_FILES="$(git diff --name-only "$MERGE_BASE" "$HEAD_REF")"

# ---------------------------------------------------------------------------
# Signal 1 — paths that are worth reviewing whatever their contents say.
# Every segment is slash-anchored so it matches a whole directory component.
# Keep in sync with .github/CODEOWNERS.
# ---------------------------------------------------------------------------
# `scripts/rails/` is here because those scripts ARE the gates — a change to
# run-gates.sh is a change to what gets reviewed, exactly like a change to
# .github/. It is the only addition to the original list; everything else is
# preserved verbatim, so this trigger can only widen, never narrow.
PATH_RE='(^\.github/|^scripts/rails/|/Auth/|/Identity/|/Security/|/SecurityAttributes/|/Migrations/|^infra/)'

# ---------------------------------------------------------------------------
# Signal 2 — security-relevant code, by what the changed lines actually contain.
#
# Grouped by the risk each marker stands for. Markers are deliberately specific:
# a bare `Token` would match every CancellationToken in the codebase and fire on
# everything, which degrades to the same uselessness as never firing at all.
# ---------------------------------------------------------------------------
CONTENT_RE='(\[Authorize|AllowAnonymous|ClaimsPrincipal|RequireClaim|AuthenticationScheme|AuthorizationPolicy|IAuthorizationHandler'          # authn/authz
CONTENT_RE="${CONTENT_RE}"'|OwnerId|TenantId|KnowledgeScope|VisibleTo|WritableBy'                                                             # scope isolation — this repo's recurring defect class
CONTENT_RE="${CONTENT_RE}"'|CapabilityEnvelope|AutonomyLevel|AllowedTools|DeniedTools|Sandbox|GovernanceTrace|PermittedApprovers|Approver'    # capability + governance
CONTENT_RE="${CONTENT_RE}"'|Jwt|Bearer|AccessToken|RefreshToken|SasToken|ApiKey|Password|Secret|Hmac|Sha256|RandomNumberGenerator'            # credentials + crypto
CONTENT_RE="${CONTENT_RE}"'|\[Http(Get|Post|Put|Delete|Patch)|MapGet\(|MapPost\(|MapDelete\(|UseCors|RateLimit'                               # externally reachable surface
CONTENT_RE="${CONTENT_RE}"'|Sanitiz|Redact|Scrub)'                                                                                            # output safety

# Prose is excluded from the content scan — a security guide legitimately says
# "password" on every page. Everything else that ships or executes is scanned,
# including scripts/ and the rails themselves, so a file is never in scope for the
# gate but out of scope for the reviewer.
# This script and its tests are excluded because they DEFINE the marker list rather
# than use it: scanning them matches every marker at once and drowns the `signals`
# output in noise. They still reach the reviewer through the path list above, which
# is the stronger trigger anyway — so this exclusion cannot hide a change to them.
scannable() {
  printf '%s' "$CHANGED_FILES" \
    | grep -vE '(^documentation/|\.md$|^\.github/scripts/security-gate-scope(\.test)?\.sh$)' || true
}

# Added/removed lines only; the +++/--- file headers are not content.
diff_lines() { # <pathspec...>
  git diff "$MERGE_BASE" "$HEAD_REF" -- "$@" | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' || true
}

SCANNABLE_FILES="$(scannable)"
SIGNALS=""
if [ -n "$SCANNABLE_FILES" ]; then
  # shellcheck disable=SC2046 — the file list is git output, one path per line.
  SIGNALS="$(printf '%s\n' "$SCANNABLE_FILES" | tr '\n' '\0' | xargs -0 -r git diff "$MERGE_BASE" "$HEAD_REF" -- \
    | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' | grep -oE "$CONTENT_RE" | sort -u | paste -sd, - || true)"
fi

PATH_FILES="$(printf '%s' "$CHANGED_FILES" | grep -E "$PATH_RE" || true)"

# The two signals are ADDITIVE, never exclusive. An earlier draft made the path
# branch an `elif`, so a PR touching one .github/ file plus src/ tenant-scoping code
# was handed a scope file naming only the .github file — and the reviewer is told to
# start there. That is a hole big enough to walk an owner-check regression through,
# and this very change was an instance of it. Compute both, union the file lists.
CONTENT_FILES=""
if [ -n "$SIGNALS" ]; then
  # Name the files whose OWN changed lines carry a marker, so the reviewer reads the
  # security-relevant part of a large diff first rather than end to end.
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    if diff_lines "$f" | grep -qE "$CONTENT_RE"; then
      CONTENT_FILES="${CONTENT_FILES}${f}"$'\n'
    fi
  done <<EOF
$SCANNABLE_FILES
EOF
fi

REQUIRED=false
TRIGGER=none
REASON="no gated path and no security-relevant code changed"

if [ -n "$PATH_FILES" ] && [ -n "$SIGNALS" ]; then
  REQUIRED=true; TRIGGER="path+content"
  REASON="gated path changed AND security-relevant code changed"
elif [ -n "$PATH_FILES" ]; then
  REQUIRED=true; TRIGGER="path"
  REASON="gated path changed (auth/identity/security/migrations/.github/rails/infra)"
elif [ -n "$SIGNALS" ]; then
  REQUIRED=true; TRIGGER="content"
  REASON="security-relevant code changed"
fi

SCOPED_FILES="$(printf '%s\n%s\n' "$PATH_FILES" "$CONTENT_FILES" | grep -E . | sort -u || true)"

# A scope file must never say "nothing" while the gate says "review this". If the
# union came out empty despite a trigger, hand over the whole diff — an over-wide
# scope costs turns; an empty one reads as "there is nothing here".
if [ "$REQUIRED" = "true" ] && [ -z "$SCOPED_FILES" ]; then
  SCOPED_FILES="$CHANGED_FILES"
fi

if [ -n "${SECURITY_SCOPE_FILE:-}" ]; then
  printf '%s\n' "$SCOPED_FILES" | grep -E . > "$SECURITY_SCOPE_FILE" || : > "$SECURITY_SCOPE_FILE"
fi

emit() {
  printf 'required=%s\n' "$REQUIRED"
  printf 'trigger=%s\n' "$TRIGGER"
  printf 'reason=%s\n' "$REASON"
  printf 'signals=%s\n' "$SIGNALS"
}

emit
if [ "$FORMAT" = "github" ] && [ -n "${GITHUB_OUTPUT:-}" ]; then
  emit >> "$GITHUB_OUTPUT"
fi
