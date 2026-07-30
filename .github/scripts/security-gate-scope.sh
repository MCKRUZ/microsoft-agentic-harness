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
#   trigger=path|content|none
#   reason=<one line, human-readable>
#   signals=<comma-separated markers matched, empty when trigger=path>
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
PATH_RE='(^\.github/|/Auth/|/Identity/|/Security/|/SecurityAttributes/|/Migrations/|^infra/)'

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

# Added/removed lines only (drop the +++/--- file headers), restricted to shipped
# source. Docs and scripts reach the gate through the path list when they matter.
CHANGED_LINES="$(git diff "$MERGE_BASE" "$HEAD_REF" -- 'src/*' | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' || true)"

SIGNALS="$(printf '%s' "$CHANGED_LINES" | grep -oE "$CONTENT_RE" | sort -u | paste -sd, - 2>/dev/null || true)"

REQUIRED=false
TRIGGER=none
REASON="no gated path and no security-relevant code changed"
SCOPED_FILES=""

if printf '%s' "$CHANGED_FILES" | grep -Eq "$PATH_RE"; then
  REQUIRED=true
  TRIGGER=path
  SCOPED_FILES="$(printf '%s' "$CHANGED_FILES" | grep -E "$PATH_RE" || true)"
  REASON="gated path changed (auth/identity/security/migrations/.github/infra)"
  SIGNALS=""
elif [ -n "$SIGNALS" ]; then
  REQUIRED=true
  TRIGGER=content
  # Name the files whose own changed lines carry a marker, so the reviewer reads
  # the security-relevant part of a large diff rather than all of it.
  for f in $(printf '%s' "$CHANGED_FILES" | grep -E '^src/.*\.(cs|csproj|json|props|targets)$' || true); do
    if git diff "$MERGE_BASE" "$HEAD_REF" -- "$f" | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' | grep -qE "$CONTENT_RE"; then
      SCOPED_FILES="${SCOPED_FILES}${f}"$'\n'
    fi
  done
  REASON="security-relevant code changed under src/"
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
