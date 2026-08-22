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
#   It is not meant to be invoked with -Kind run-gates by hand.
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
# Honest scope: for -Kind code-review / simplify, the receipt's CONTENT is whatever is
# piped in; this script binds it to the reviewed code and timestamps it, but it cannot
# verify the review was done well — that is on the reviewer, and ultimately on CI, which
# re-derives its verdict server-side. This script's value there is mechanical: the push is
# blocked until code-bound review evidence exists, turning "might forget entirely" into
# "must produce inspectable review evidence."
#
# -Kind run-gates is different in kind: it is written by run-gates.sh itself, not typed by
# an agent, so its existence is proof the local gates actually ran and actually passed
# against this exact diff — not just a claim that they did.

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('code-review', 'simplify', 'run-gates')]
  [string]$Kind
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
if (-not $summary) { $summary = "($Kind run; no summary piped)" }

# Record the covered file list in the receipt so a human can audit what the review saw,
# not just that some review happened.
$header = @(
  "# $Kind receipt",
  "fingerprint: $fingerprint",
  "recorded-at-commit: $sha",
  "reviewed-files:"
  ($change.Paths | ForEach-Object { "  - $_" })
  ""
) | Out-String

$path = Join-Path $receiptDir "$fingerprint.$Kind"
Set-Content -Path $path -Value ($header + $summary) -Encoding UTF8

Write-Output ("Saved $Kind receipt for fingerprint $fingerprint " +
              "($($change.Paths.Count) reviewable file(s)) at .claude/.review-receipts/$fingerprint.$Kind")
