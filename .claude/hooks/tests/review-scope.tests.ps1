#!/usr/bin/env pwsh
#
# review-scope.tests.ps1 — proves the review gate's scoping and fingerprint rules against
# REAL history in this repository, not synthetic fixtures. Every commit referenced below
# actually caused (or correctly caused) a review re-arm, so the test encodes why the
# behaviour matters rather than merely what the function returns.
#
# Run:  pwsh -NoProfile -File .claude/hooks/tests/review-scope.tests.ps1
# Exits non-zero on any failure so it can be wired into a gate later.
#
# The suite deliberately asserts in BOTH directions — pairs that must share a fingerprint
# and pairs that must not. A fingerprint function that returned a constant would fail the
# "differs" cases; one that returned noise would fail the "same" cases. Neither can pass
# this suite trivially.

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

. (Join-Path $PSScriptRoot '..' 'review-scope.ps1')

$script:Failures = 0
$script:Ran = 0

function Assert-That([string]$Name, [bool]$Condition, [string]$Detail = '') {
  $script:Ran++
  if ($Condition) {
    Write-Host "  PASS  $Name" -ForegroundColor Green
  } else {
    $script:Failures++
    Write-Host "  FAIL  $Name" -ForegroundColor Red
    if ($Detail) { Write-Host "        $Detail" -ForegroundColor Red }
  }
}

function Get-Fp([string]$Base, [string]$Head) {
  $change = Get-ReviewableChange -Base $Base -Head $Head
  return (Get-ReviewFingerprint -Change $change)
}

Write-Host ''
Write-Host 'Scope: which files count as reviewable' -ForegroundColor Cyan

# 175 tracked .tsx files exist across both frontends and were NOT matched by the gate's
# original pattern, so every React component escaped review. This is the coverage fix.
Assert-That 'a .tsx component under src/ is reviewable' `
  ('src/Content/Presentation/Presentation.Dashboard/src/routes/Cost/CostPage.tsx' -match $script:ReviewablePattern)

Assert-That 'a .ts module under src/ is reviewable' `
  ('src/Content/Presentation/Presentation.Dashboard/src/lib/metricCatalog.ts' -match $script:ReviewablePattern)

Assert-That 'a .cs file under src/ is reviewable' `
  ('src/Content/Domain/Domain.AI/Skills/SkillAgentOptions.cs' -match $script:ReviewablePattern)

Assert-That 'a first-party eslint.config.js is reviewable' `
  ('src/Content/Presentation/Presentation.WebUI/eslint.config.js' -match $script:ReviewablePattern)

# json is excluded on purpose: package-lock.json churns by hundreds of lines per dependency
# bump, and gating on it would reintroduce the very waste this design removes.
Assert-That 'package-lock.json is NOT reviewable' `
  (-not ('src/Content/Presentation/Presentation.WebUI/package-lock.json' -match $script:ReviewablePattern))

Assert-That 'a markdown file under src/ is NOT reviewable' `
  (-not ('src/Content/Application/Application.AI.Common/README.md' -match $script:ReviewablePattern))

Assert-That 'a file outside src/ is NOT reviewable' `
  (-not ('documentation/onboarding/05-skills.html' -match $script:ReviewablePattern))

Write-Host ''
Write-Host 'Economy: non-source commits must NOT re-arm the gate' -ForegroundColor Cyan

# PR #220 (base 6f53d285): d91946cf changed src/Directory.Packages.props; 0cea903b and
# 504ef90d changed only .github/**. Under the old SHA binding all three demanded fresh
# /code-review AND /simplify receipts — four review passes spent re-reading identical
# source. All three must now share one fingerprint.
$fp220_src     = Get-Fp '6f53d285' 'd91946cf'
$fp220_github1 = Get-Fp '6f53d285' '0cea903b'
$fp220_github2 = Get-Fp '6f53d285' '504ef90d'

Assert-That 'PR #220 source commit produces a fingerprint' `
  ($null -ne $fp220_src) "got: $fp220_src"

Assert-That 'a .github-only follow-up keeps the same fingerprint' `
  ($fp220_src -eq $fp220_github1) "$fp220_src vs $fp220_github1"

Assert-That 'a second .github-only follow-up still keeps it' `
  ($fp220_src -eq $fp220_github2) "$fp220_src vs $fp220_github2"

# PR #224 (base c087decf): f19eb8ea changed source; 2fc6417c changed only a docs/ markdown
# file. The docs commit must not discard the review.
$fp224_src  = Get-Fp 'c087decf' 'f19eb8ea'
$fp224_docs = Get-Fp 'c087decf' '2fc6417c'

Assert-That 'a docs-only follow-up keeps the same fingerprint' `
  ($fp224_src -eq $fp224_docs) "$fp224_src vs $fp224_docs"

Write-Host ''
Write-Host 'Thoroughness: real source changes MUST re-arm the gate' -ForegroundColor Cyan

# 25519cc5 edited two .cs files on top of the docs commit. Its re-arm was legitimate and
# must survive this change — this is the guard against buying economy with coverage.
$fp224_code2 = Get-Fp 'c087decf' '25519cc5'

Assert-That 'adding .cs edits changes the fingerprint' `
  ($fp224_docs -ne $fp224_code2) "$fp224_docs vs $fp224_code2"

# PR #225 (base 5de3fa1d): both commits changed C#. 46932441 fixed a real over-disclosure
# defect found by reviewing 45c45a09, so the re-arm between them had to happen.
$fp225_first  = Get-Fp '5de3fa1d' '45c45a09'
$fp225_second = Get-Fp '5de3fa1d' '46932441'

Assert-That 'PR #225 first commit produces a fingerprint' `
  ($null -ne $fp225_first) "got: $fp225_first"

Assert-That 'a follow-up source fix changes the fingerprint' `
  ($fp225_first -ne $fp225_second) "$fp225_first vs $fp225_second"

Write-Host ''
Write-Host 'Degenerate cases' -ForegroundColor Cyan

# A range touching no reviewable source must yield no fingerprint at all, so the gate
# allows the push rather than demanding a receipt that could never be satisfied.
$fpNone = Get-Fp '0cea903b' '504ef90d'
Assert-That 'a range with no reviewable change yields no fingerprint' `
  ($null -eq $fpNone) "got: $fpNone"

Assert-That 'a null change yields no fingerprint' `
  ($null -eq (Get-ReviewFingerprint -Change $null))

Write-Host ''
if ($script:Failures -gt 0) {
  Write-Host "$script:Failures of $script:Ran assertions FAILED" -ForegroundColor Red
  exit 1
}
Write-Host "All $script:Ran assertions passed" -ForegroundColor Green
exit 0
