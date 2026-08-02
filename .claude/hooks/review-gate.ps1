#!/usr/bin/env pwsh
#
# review-gate.ps1 — the methodology's "review gate" (companion to review-cadence.md).
#
# Fires on the Claude Code `PreToolUse` event for Bash. Before the agent is allowed
# to `git push` or `gh pr create` a change that touches compilable source, this proves
# that `/code-review` AND `/simplify` were actually run against the EXACT code being
# pushed — not the agent's recollection that it did them. A missing receipt blocks the
# push and tells the agent to run the reviews.
#
# Why a gate and not a reminder: a written instruction to "run code-review before
# pushing" is the same class of thing the agent forgets. The only reliable enforcement
# is mechanical — the push refuses until per-change review evidence exists. This mirrors
# the borrowed principle: don't trust the agent to behave, force it with plain machinery.
#
# Scope decisions (deliberate, documented):
#   * Only gates pushes/PRs whose branch diff vs the base touches reviewable source. The
#     file list lives in review-scope.ps1 and is shared with the receipt writer.
#   * Receipts are bound to a FINGERPRINT OF THE REVIEWABLE DIFF, not to the commit SHA.
#     Reviewing code and then committing a markdown fix no longer discards the review;
#     changing a single line of source still does. See review-scope.ps1 for the measured
#     rationale — 6 of 7 receipted commits here changed no C# at all.
#   * The working tree must be clean under src/ so HEAD is what actually gets pushed.
#   * Skill-agnostic: it checks for review RECEIPTS, not for a specific tool. Receipts
#     are written by save-review-receipt.ps1 when /code-review and /simplify complete.
#   * Honors RAILS_SKIP_REVIEW_GATE=1 as a documented, auditable escape hatch.
#   * Never wedges the session on missing tooling or an unresolvable base: it fails OPEN
#     (allows) only when it genuinely cannot compute the diff, and fails CLOSED (blocks)
#     whenever it can prove review evidence is missing.
#
# IMPORTANT — coverage boundary: a Claude Code hook only fires for actions taken THROUGH
# Claude Code. A human running `git push` in their own terminal is NOT gated by this; the
# server-side equivalent is a required CI check. This gate stops the agent from skipping
# review, not every possible push.
#
# IMPORTANT — trust boundary: a receipt's CONTENT is whatever was piped into the writer.
# This gate proves a receipt exists for this exact code; it cannot prove the review was
# done well, or done at all. The non-forgeable enforcement is CI (correctness-review,
# security-review, grader, OWASP), which re-derives its verdict server-side.
#
# Contract: emit a PreToolUse hookSpecificOutput with permissionDecision "deny" to block;
# emit nothing (exit 0) to allow.

$ErrorActionPreference = 'Stop'
# Read native command exit codes explicitly so a non-zero git call never throws before
# we can decide — this gate must fail closed on "evidence missing", open on "can't tell".
$PSNativeCommandUseErrorActionPreference = $false

function Allow { exit 0 }
function Deny([string]$reason) {
  @{
    hookSpecificOutput = @{
      hookEventName          = 'PreToolUse'
      permissionDecision     = 'deny'
      permissionDecisionReason = $reason
    }
  } | ConvertTo-Json -Compress -Depth 5
  exit 0
}

# --- Read the PreToolUse payload from stdin.
$raw = [Console]::In.ReadToEnd()
$payload = $null
if ($raw) { try { $payload = $raw | ConvertFrom-Json } catch { } }
if (-not $payload) { Allow }

# --- Only gate Bash commands that actually INVOKE git push / gh pr create. Match the
#     start of each shell segment (split on ; && || | &) so the words appearing inside a
#     quoted string, an echo, or `--help` text don't trip the gate. `git -C <path> push`
#     is recognized.
if ($payload.tool_name -ne 'Bash') { Allow }
$cmd = [string]$payload.tool_input.command
if (-not $cmd) { Allow }
$gates = $false
foreach ($seg in ($cmd -split '\|\||&&|[;|&]')) {
  $s = $seg.Trim()
  if ($s -match '^git\s+(-C\s+\S+\s+)?push(\s|$)' -or $s -match '^gh\s+pr\s+create(\s|$)') {
    $gates = $true; break
  }
}
if (-not $gates) { Allow }

# --- Documented escape hatch.
if ($env:RAILS_SKIP_REVIEW_GATE -eq '1') {
  [Console]::Error.WriteLine('review-gate: RAILS_SKIP_REVIEW_GATE=1 set; skipping review gate.')
  Allow
}

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location $projectDir

# --- Tooling guard: don't trap the agent if git isn't available.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  [Console]::Error.WriteLine('review-gate: git not found; skipping review gate.')
  Allow
}

# Shared scope + fingerprint logic, so the enforcer and the receipt writer cannot drift.
. (Join-Path $PSScriptRoot 'review-scope.ps1')

# --- Scope: only reviewable source triggers the gate.
#     $null here means EITHER "nothing reviewable changed" OR "git could not compute the
#     diff at all". Both allow, which is the pre-existing documented behaviour: the gate
#     fails open when it cannot tell, and fails closed only when it can prove evidence is
#     missing. The two are deliberately not distinguished — a gate that wedges the session
#     on an unreadable repo gets disabled by the first person it blocks.
$change = Get-ReviewableChange -Base (Resolve-ReviewBase)
if (-not $change) { Allow }

# --- The committed code under review must equal what gets pushed. Deliberately broad:
#     ANY dirty path under src/ blocks, not just reviewable ones. That is the fail-closed
#     direction, and it costs nothing when the tree is clean, which is the normal case.
$dirtySrc = & git status --porcelain -- 'src' 2>$null
if ($dirtySrc) {
  Deny("Review gate: you have uncommitted changes under src/. Commit them first so the " +
       "review covers exactly what you push, then run /code-review and /simplify on the final code.")
}

# --- Require a receipt for each review, bound to the exact code being pushed.
$fingerprint = Get-ReviewFingerprint -Change $change
if (-not $fingerprint) {
  Deny("Review gate: reviewable source changed under src/, but the review fingerprint could " +
       "not be computed, so review evidence cannot be verified. Re-run the push; if it persists, " +
       "inspect .claude/hooks/review-scope.ps1. Emergency bypass: set RAILS_SKIP_REVIEW_GATE=1.")
}

$receiptDir = Join-Path $projectDir '.claude/.review-receipts'
$missing = @()
if (-not (Test-Path (Join-Path $receiptDir "$fingerprint.code-review"))) { $missing += '/code-review' }
if (-not (Test-Path (Join-Path $receiptDir "$fingerprint.simplify")))    { $missing += '/simplify' }

if ($missing.Count -gt 0) {
  Deny("Review gate: src/ changed but [$($missing -join ', ')] " +
       "$(if ($missing.Count -eq 1) {'has'} else {'have'}) not been run against the current code " +
       "(fingerprint $fingerprint, $($change.Paths.Count) reviewable file(s)). " +
       "Run the missing review(s), then record each by piping its summary to " +
       ".claude/hooks/save-review-receipt.ps1 (e.g. `'...summary...' | pwsh -NoProfile -File " +
       ".claude/hooks/save-review-receipt.ps1 -Kind code-review`). Commits that change no " +
       "reviewable source (docs, workflows) do NOT invalidate an existing receipt. " +
       "Emergency bypass: set RAILS_SKIP_REVIEW_GATE=1.")
}

Allow
