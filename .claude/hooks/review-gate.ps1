#!/usr/bin/env pwsh
#
# review-gate.ps1 — the methodology's "review gate" (companion to review-cadence.md).
#
# Fires on the Claude Code `PreToolUse` event for Bash. Before the agent is allowed
# to `git push` or `gh pr create` a change that touches compilable source, this proves
# that `/code-review` AND `/simplify` were actually run against the EXACT commit being
# pushed — not the agent's recollection that it did them. A missing receipt blocks the
# push and tells the agent to run the reviews.
#
# Why a gate and not a reminder: a written instruction to "run code-review before
# pushing" is the same class of thing the agent forgets. The only reliable enforcement
# is mechanical — the push refuses until per-commit review evidence exists. This mirrors
# the borrowed principle: don't trust the agent to behave, force it with plain machinery.
#
# Scope decisions (deliberate, documented):
#   * Only gates pushes/PRs whose branch diff vs the base touches compilable source
#     (src/**). Docs-, memory-, and config-only pushes pass without review receipts.
#   * Receipts are bound to the current HEAD short-SHA, so a review of older code does
#     not satisfy a later commit. The working tree must be clean under src/ so HEAD is
#     what actually gets pushed.
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

# --- Resolve the base to diff against. Prefer origin/main, then main. If neither
#     resolves (unusual), fall back to the last commit so we can still scope on src/.
$base = $null
foreach ($ref in @('origin/main', 'main')) {
  $resolved = & git rev-parse --verify --quiet "$ref" 2>$null
  if ($LASTEXITCODE -eq 0 -and $resolved) { $base = $ref; break }
}

if ($base) {
  $changed = & git diff --name-only "$base...HEAD" 2>$null
} else {
  $changed = & git show --name-only --pretty=format: HEAD 2>$null
}

# Can't compute a diff at all — fail open rather than wedge the session.
if ($LASTEXITCODE -ne 0) {
  [Console]::Error.WriteLine('review-gate: could not compute branch diff; skipping.')
  Allow
}

# --- Scope: only compilable source under src/ triggers the gate.
$srcChanged = $changed | Where-Object { $_ -match '^src/.*\.(cs|csproj|slnx|props|targets|razor|cshtml|ts|html)$' }
if (-not $srcChanged) { Allow }

# --- The committed code under review must equal what gets pushed.
$dirtySrc = & git status --porcelain -- 'src' 2>$null
if ($dirtySrc) {
  Deny("Review gate: you have uncommitted changes under src/. Commit them first so the " +
       "review covers exactly what you push, then run /code-review and /simplify on the final commit.")
}

# --- Require a receipt for each review, bound to the exact commit being pushed.
$sha = (& git rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $sha) { Allow }   # detached/unborn — can't bind; don't wedge.
$sha = $sha.Trim()

$receiptDir = Join-Path $projectDir '.claude/.review-receipts'
$missing = @()
if (-not (Test-Path (Join-Path $receiptDir "$sha.code-review"))) { $missing += '/code-review' }
if (-not (Test-Path (Join-Path $receiptDir "$sha.simplify")))    { $missing += '/simplify' }

if ($missing.Count -gt 0) {
  Deny("Review gate: src/ changed but [$($missing -join ', ')] " +
       "$(if ($missing.Count -eq 1) {'has'} else {'have'}) not been run against commit $sha. " +
       "Run the missing review(s), then record each by piping its summary to " +
       ".claude/hooks/save-review-receipt.ps1 (e.g. `'...summary...' | pwsh -NoProfile -File " +
       ".claude/hooks/save-review-receipt.ps1 -Kind code-review`). Re-run after re-committing any " +
       "fixes the reviews produce. Emergency bypass: set RAILS_SKIP_REVIEW_GATE=1.")
}

Allow
