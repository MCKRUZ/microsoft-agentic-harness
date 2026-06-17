#!/usr/bin/env pwsh
#
# save-review-receipt.ps1 — records that a review ran against the current commit, for the
# review gate (review-gate.ps1) to verify before a push/PR.
#
# Usage (pipe the review summary on stdin so the receipt is real evidence, not a flag):
#   "...code-review findings...""  | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind code-review
#   "...simplify findings..."       | pwsh -NoProfile -File .claude/hooks/save-review-receipt.ps1 -Kind simplify
#
# The receipt is written to .claude/.review-receipts/<HEAD-short-sha>.<kind> so it is bound
# to the exact commit. If you amend or add commits, the SHA changes and the gate re-arms,
# forcing a fresh review of the final code. Receipts are gitignored (per-clone evidence).
#
# Honest scope: the receipt's CONTENT is whatever is piped in; this script binds it to the
# commit and timestamps it, but it cannot verify the review was done well — that is on the
# reviewer. Its value is mechanical: the push is blocked until a commit-bound receipt exists,
# turning "might forget entirely" into "must produce per-commit, inspectable review evidence."

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('code-review', 'simplify')]
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

$sha = (& git rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $sha) {
  [Console]::Error.WriteLine('save-review-receipt: cannot resolve HEAD.'); exit 1
}
$sha = $sha.Trim()

$receiptDir = Join-Path $projectDir '.claude/.review-receipts'
New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null

$summary = [Console]::In.ReadToEnd()
if (-not $summary) { $summary = "($Kind run; no summary piped)" }

$header = "# $Kind receipt`ncommit: $sha`n`n"
$path = Join-Path $receiptDir "$sha.$Kind"
Set-Content -Path $path -Value ($header + $summary) -Encoding UTF8

Write-Output "Saved $Kind receipt for commit $sha at .claude/.review-receipts/$sha.$Kind"
