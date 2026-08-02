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
# core.quotePath=false on every invocation: git otherwise emits non-ASCII paths
# octal-escaped and quoted ("cafÃ©.cs"), and feeding that back as a pathspec
# matches nothing — a file named Order.cs with an accent would change OwnerId
# scoping and raise no signal at all.
git() { command git -c core.quotePath=false "$@"; }

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
# .github/. Additions only; everything from the original list is preserved
# verbatim, so this trigger can only widen, never narrow.
#
# `.claude/hooks/` is the same argument one directory over, and it was missed.
# PR #226 rewrote the push gate — review-gate.ps1, review-scope.ps1,
# save-review-receipt.ps1 and both of their test suites, five files that decide
# whether ANY review happens at all — and raised no path signal, because the list
# named the rails but not the hooks. A change to the thing that decides what gets
# reviewed is the change most worth reviewing.
#
# `.claude/settings.json` is listed with them because it is where the hooks are WIRED.
# Gating the hook scripts while leaving their registration ungated protects the lock
# and not the door: deleting the PreToolUse entry disables the push gate completely
# without touching a single gated file. `.json` is excluded from the CONTENT scan by
# design (package-lock churn), so only this path entry can catch it.
PATH_RE='(^\.github/|^scripts/rails/|^\.claude/(hooks/|settings\.json$)|/Auth/|/Identity/|/Security/|/SecurityAttributes/|/Migrations/|^infra/)'

# ---------------------------------------------------------------------------
# Signal 2 — security-relevant code, by what the changed lines actually contain.
#
# Grouped by the risk each marker stands for. Markers are deliberately specific:
# a bare `Token` would match every CancellationToken in the codebase and fire on
# everything, which degrades to the same uselessness as never firing at all.
#
# THE LIST IS A VOCABULARY, AND A MISSING WORD IS A MISSING GATE.
# The first five groups describe authorization, scope isolation, capability,
# credentials, and reachable surface. They say nothing about the filesystem — so
# PR #215, which was *entirely* path confinement (canonicalisation, per-segment
# symlink resolution, a root-prefix comparison, a confinement latch), raised zero
# signals and the reviewer was skipped. It reported `pass` in five seconds. The
# code had been reviewed only because a human ran the reviewer by hand.
#
# That is the same failure as the folder-only filter above, one category over: the
# gate did not fire on the changes it exists to review. The last two groups close
# the two categories this repo actually has code in — filesystem confinement, and
# code execution / unsafe deserialization.
#
# COUNTS ARE PER TRACKED FILE, NOT PER GREP HIT. Measure with `git ls-files`, never
# a bare recursive grep: bin/ and obj/ inflate some markers more than tenfold (AntiSSRF
# reads as 1101 hits and 28 tracked files), and build artifacts can never appear in a
# diff. A marker judged on the inflated number gets rejected for noise it cannot cause.
#
# DELIBERATELY EXCLUDED, so nobody "completes" the list later and breaks it:
#   HttpClient (101 of 3,208 tracked .cs files) and JsonSerializer.Deserialize (57)
#   are real SSRF and deserialization signals in the abstract, and useless here — they
#   appear in nearly every connector and DTO, so adding them makes the gate fire on
#   every PR. A gate that always fires is triaged as noise and is worth no more than
#   one that never fires.
#
#   SSRF is therefore covered by naming the GUARD rather than the mechanism:
#   EgressPolicy / EgressAllowlist / AntiSSRF / Ssrf. A PR that weakens or bypasses
#   egress policy is the SSRF change actually worth reviewing, and those types appear
#   only in code that is about egress control — so the signal stays specific while
#   every ordinary outbound call stays silent. Both halves are pinned by tests:
#   touching the policy fires, holding an HttpClient does not.
#
#   ChatMessage (115 tracked files) is excluded for the same reason as HttpClient: it
#   is ordinary agent plumbing present in nearly every conversation path. AIContext is
#   in, at 27 — the two words look adjacent and are two orders of magnitude apart in
#   how often they appear. Count before adding.
#
# THE GAP THAT ADDED THE LAST TWO GROUPS
# --------------------------------------
# PR #227 moved GoverningToolContextProvider off AIContextProvider's ADDITIVE
# ProvideAIContextAsync hook onto InvokingCoreAsync. Implemented on the additive hook
# the provider was inert: the base merge restores everything it dropped and publishes
# an unwrapped copy of everything it wrapped, so a tool-permission control that reads
# as enforcing enforced nothing. That is a security control that was dead in production —
# and this gate scored the PR that fixed it required=false, trigger=none, signals=(none).
# Replayed against the isolated fix commit alone it still scored zero, so it was not
# dilution by a large diff; the vocabulary simply had no word for the agent-context
# surface. CLAUDE.md records this same defect landing four separate times.
#
# The gate is now on its third widening of exactly this shape — folder-only missed
# #203-#210, the content list missed #215's path confinement, and it missed #227's
# agent context. Each time the fix was a category the list had no word for, so the two
# groups below name this repo's remaining AI-security surfaces rather than only the one
# that just bit: ambient scope propagation (AsyncLocal is how knowledge scope reaches
# child scopes and post-turn background writes), the sanctioned identity resolver
# (GetUserIdOrNull — CLAUDE.md forbids a second precedence ladder, so a change here is
# the scope-isolation defect class), approval bypass, and content-safety.
# ---------------------------------------------------------------------------
CONTENT_RE='(\[Authorize|AllowAnonymous|ClaimsPrincipal|RequireClaim|AuthenticationScheme|AuthorizationPolicy|IAuthorizationHandler'          # authn/authz
CONTENT_RE="${CONTENT_RE}"'|OwnerId|TenantId|KnowledgeScope|VisibleTo|WritableBy'                                                             # scope isolation — this repo's recurring defect class
CONTENT_RE="${CONTENT_RE}"'|CapabilityEnvelope|AutonomyLevel|AllowedTools|DeniedTools|Sandbox|GovernanceTrace|PermittedApprovers|Approver'    # capability + governance
CONTENT_RE="${CONTENT_RE}"'|Jwt|Bearer|AccessToken|RefreshToken|SasToken|ApiKey|Password|Secret|Hmac|Sha256|RandomNumberGenerator'            # credentials + crypto
CONTENT_RE="${CONTENT_RE}"'|\[Http(Get|Post|Put|Delete|Patch)|MapGet\(|MapPost\(|MapDelete\(|UseCors|RateLimit'                               # externally reachable surface
CONTENT_RE="${CONTENT_RE}"'|GetFullPath|ResolveLinkTarget|LinkTarget|IsPathRooted|GetRelativePath|DirectorySeparatorChar'                     # filesystem path confinement
CONTENT_RE="${CONTENT_RE}"'|EgressPolicy|EgressAllowlist|AntiSSRF|Ssrf|SSRF'                                                                   # egress control (the SSRF guard, not the mechanism)
CONTENT_RE="${CONTENT_RE}"'|FromSqlRaw|FromSqlInterpolated|ExecuteSqlRaw|ExecuteSqlInterpolated'                                               # raw SQL — the one escape from EF Core's parameterisation
CONTENT_RE="${CONTENT_RE}"'|Process\.Start|ProcessStartInfo|ZipArchive|ExtractToDirectory|BinaryFormatter|TypeNameHandling|XmlSerializer'     # code execution + unsafe deserialization
CONTENT_RE="${CONTENT_RE}"'|Sanitiz|Redact|Scrub'                                                                                             # output safety
# AIContext subsumes AIContextProvider (27 tracked files vs 25) and so also fires on a
# line that merely builds or returns the context, not only on one naming the base type.
CONTENT_RE="${CONTENT_RE}"'|AIContext|InvokingCoreAsync|ProvideAIContextAsync|GovernedAIFunction|ToolPermissionFilter|ReservedPlanCapability'  # agent context merge + tool governance
CONTENT_RE="${CONTENT_RE}"'|GetUserIdOrNull|AsyncLocal|AutoApprove|HumanGate|ContentSafety|PromptInjection)'                                   # identity, ambient scope, approval bypass, content safety

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
