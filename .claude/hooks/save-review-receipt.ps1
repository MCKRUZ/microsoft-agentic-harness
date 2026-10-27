#!/usr/bin/env pwsh
#
# save-review-receipt.ps1 — records that a review ran against the current code, for the
# review gate (review-gate.ps1) to verify before a push/PR.
#
# Usage (pipe the review summary on stdin so the receipt is real evidence, not a flag):
#   "...code-review findings..." | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review
#   "...simplify findings..."    | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind simplify
#   scripts/rails/run-gates.sh writes its OWN "run-gates" receipt automatically on a
#   full, default-base, all-gates-passed run — see the block near the end of that script.
#   It is the INTENDED writer for -Kind run-gates; nothing here or in review-gate.ps1
#   enforces that, though (see "Honest scope" below) — do not invoke it by hand for
#   -Kind run-gates unless you are genuinely reporting a run-gates.sh pass that already
#   happened outside this script's own automatic call.
#
# The receipt is written to .claude/.review-receipts/<fingerprint>.<kind>, where the
# fingerprint identifies the reviewable source diff itself (see review-scope.ps1) rather
# than the commit that happens to contain it. Consequences, both intended:
#   * Editing source and re-committing changes the fingerprint, so the gate re-arms and
#     forces a fresh review of the final code — same as before.
#   * Committing docs, workflows, or anything else non-reviewable leaves the fingerprint
#     alone, so an existing receipt still applies and no re-review is demanded.
# Receipts are gitignored (per-clone evidence).
#
# Honest scope, ALL THREE KINDS: a receipt's CONTENT is whatever is piped in — for
# code-review/simplify that's a typed summary, for run-gates it's a summary run-gates.sh
# assembles from its own PASSED array. This script binds it to the reviewed code and
# timestamps it, but nothing here can verify the underlying check was actually done, let
# alone done well — any command with shell access (an agent included) can produce any of
# the three receipts without running the thing it claims to attest to. That is on the
# reviewer/script to do honestly, and ultimately on CI, which re-derives its own verdict
# server-side regardless of what a local receipt says. This script's value is mechanical,
# not cryptographic: the push is blocked until code-bound evidence exists for all three,
# turning "might forget entirely" into "must produce inspectable evidence" — a real
# improvement over nothing, not a guarantee against a determined or careless bypass.

#
# Convergence (2026-10-27; Anthropic, "How the Claude Code team does code review" — one PR ran
# 50+ review rounds overnight, ~59% of the repeat findings were code present since round 1):
#   * -Round <n> records which local /code-review round this receipt closes.
#   * -FindingsFile <path> attaches a JSON array of findings with their disposition, the
#     article's noise measure: [{"anchor":"src/X.cs:42","class":"null-deref","tag":"important",
#     "disposition":"fixed"}]. Values are validated against .github/RAILS.md "Review conventions".
#   * -Kind round-cap is written when the third round still has open Important findings. It
#     never satisfies the gate; review-gate.ps1 denies the push until -Kind round-cap-ack exists.
#   * -Kind round-cap-ack records Matt's decision to push anyway. Pipe his words verbatim. Like
#     every receipt, its content is only as honest as the writer (see "Honest scope" above).

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('code-review', 'simplify', 'run-gates', 'round-cap', 'round-cap-ack')]
  [string]$Kind,

  [ValidateRange(1, 99)]
  [int]$Round = 0,

  [string]$FindingsFile
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location $projectDir

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  [Console]::Error.WriteLine('save-review-receipt: git not found.'); exit 1
}

# Shared scope + fingerprint logic, so the receipt is named by exactly the rule the gate
# will later check against.
. (Join-Path $PSScriptRoot 'review-scope.ps1')

$change = Get-ReviewableChange -Base (Resolve-ReviewBase)
if (-not $change) {
  [Console]::Error.WriteLine(
    'save-review-receipt: no reviewable source changed vs the base, so there is nothing for ' +
    'the gate to require a receipt for. Nothing written.')
  exit 0
}

$fingerprint = Get-ReviewFingerprint -Change $change
if (-not $fingerprint) {
  [Console]::Error.WriteLine('save-review-receipt: could not compute the review fingerprint.'); exit 1
}

$sha = (& git rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $sha) { $sha = '(unresolved)' } else { $sha = $sha.Trim() }

$receiptDir = Join-Path $projectDir '.claude/.review-receipts'
New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null

$summary = [Console]::In.ReadToEnd()
if ($Kind -eq 'round-cap-ack' -and -not "$summary".Trim()) {
  [Console]::Error.WriteLine('save-review-receipt: a round-cap-ack must carry Matt''s words. Nothing written.'); exit 1
}
if (-not $summary) { $summary = "($Kind run; no summary piped)" }

$findingsBlock = ''
if ($FindingsFile) {
  if (-not (Test-Path -LiteralPath $FindingsFile -PathType Leaf)) {
    [Console]::Error.WriteLine("save-review-receipt: findings file not found: $FindingsFile"); exit 1
  }
  try {
    $findings = @(Get-Content -LiteralPath $FindingsFile -Raw | ConvertFrom-Json)
  } catch {
    [Console]::Error.WriteLine("save-review-receipt: findings file is not valid JSON: $($_.Exception.Message)"); exit 1
  }
  $tags = 'important', 'nit', 'pre-existing'
  $dispositions = 'fixed', 'wrong', 'intentional', 'didnt-matter'
  $i = 0
  foreach ($f in $findings) {
    $i++
    $problems = @()
    if ([string]$f.anchor -notmatch '^[^\s:]+:\d+$') { $problems += "anchor '$($f.anchor)' is not <path>:<line>" }
    if ([string]$f.class -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { $problems += "class '$($f.class)' is not kebab-case" }
    if ($tags -notcontains [string]$f.tag) { $problems += "tag '$($f.tag)' is not one of $($tags -join '|')" }
    if ($dispositions -notcontains [string]$f.disposition) { $problems += "disposition '$($f.disposition)' is not one of $($dispositions -join '|')" }
    if ($problems) {
      [Console]::Error.WriteLine("save-review-receipt: finding #$i is invalid: $($problems -join '; '). Nothing written."); exit 1
    }
  }
  $findingsBlock = "findings: " + (ConvertTo-Json -InputObject $findings -Compress -Depth 4) + "`n"
}

# Record the covered file list in the receipt so a human can audit what the review saw,
# not just that some review happened.
$header = @(
  "# $Kind receipt",
  "fingerprint: $fingerprint",
  "recorded-at-commit: $sha",
  $(if ($Round -gt 0) { "round: $Round" }),
  "reviewed-files:"
  ($change.Paths | ForEach-Object { "  - $_" })
  ""
) | Where-Object { $null -ne $_ } | Out-String

$path = Join-Path $receiptDir "$fingerprint.$Kind"
Set-Content -Path $path -Value ($header + $findingsBlock + $summary) -Encoding UTF8

Write-Output ("Saved $Kind receipt for fingerprint $fingerprint " +
              "($($change.Paths.Count) reviewable file(s)) at .claude/.review-receipts/$fingerprint.$Kind")
