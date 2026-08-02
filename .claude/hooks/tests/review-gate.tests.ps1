#!/usr/bin/env pwsh
#
# review-gate.tests.ps1 — end-to-end proof that the review gate actually blocks and actually
# releases, driven through its real stdin contract against a throwaway git repository.
#
# review-scope.tests.ps1 proves the scoping/fingerprint rules in isolation. This suite proves
# the decision the agent actually experiences: deny when review evidence is missing for the
# code being pushed, allow once it exists, keep allowing across a non-source commit, and deny
# again the moment source changes. A gate that never blocks is worse than no gate, so the
# blocking cases are asserted first and are not skippable.
#
# Run:  pwsh -NoProfile -File .claude/hooks/tests/review-gate.tests.ps1

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$hooksDir  = Split-Path -Parent $PSScriptRoot
$gate      = Join-Path $hooksDir 'review-gate.ps1'
$writer    = Join-Path $hooksDir 'save-review-receipt.ps1'

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

function Invoke-Gate([string]$Command, [string]$ToolName = 'Bash') {
  <#
    Drive the hook exactly as Claude Code does: a PreToolUse JSON payload on stdin. Returns
    $true when the gate DENIED (emitted a deny decision), $false when it allowed.
  #>
  $payload = @{
    tool_name  = $ToolName
    tool_input = @{ command = $Command }
  } | ConvertTo-Json -Compress -Depth 5

  $out = $payload | pwsh -NoProfile -File $gate 2>$null
  if (-not $out) { return $false }
  return ([string]$out -match '"permissionDecision"\s*:\s*"deny"')
}

# --- Build a throwaway repo so the real one is never dirtied and real receipts are untouched.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("review-gate-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$originalProjectDir = $env:CLAUDE_PROJECT_DIR
$originalSkip = $env:RAILS_SKIP_REVIEW_GATE
$env:RAILS_SKIP_REVIEW_GATE = $null

try {
  & git -C $tmp init -b main --quiet
  & git -C $tmp config user.email 'test@example.com'
  & git -C $tmp config user.name 'Gate Test'

  New-Item -ItemType Directory -Force -Path (Join-Path $tmp 'src/Content') | Out-Null
  Set-Content -Path (Join-Path $tmp 'src/Content/Widget.cs') -Value 'public class Widget { }' -Encoding UTF8
  Set-Content -Path (Join-Path $tmp 'README.md') -Value 'base' -Encoding UTF8
  & git -C $tmp add -A
  & git -C $tmp commit -q -m 'base'

  & git -C $tmp checkout -q -b feature
  Set-Content -Path (Join-Path $tmp 'src/Content/Widget.cs') -Value 'public class Widget { public int N; }' -Encoding UTF8
  & git -C $tmp add -A
  & git -C $tmp commit -q -m 'feat: change source'

  $env:CLAUDE_PROJECT_DIR = $tmp

  Write-Host ''
  Write-Host 'Gating: which commands are even considered' -ForegroundColor Cyan

  Assert-That 'a non-Bash tool is ignored' `
    (-not (Invoke-Gate -Command 'git push' -ToolName 'Edit'))

  Assert-That 'an unrelated Bash command is ignored' `
    (-not (Invoke-Gate 'git status'))

  Assert-That 'the word "git push" inside a quoted string does not trip the gate' `
    (-not (Invoke-Gate 'echo "remember to git push later"'))

  Write-Host ''
  Write-Host 'Blocking: unreviewed source must NOT reach the remote' -ForegroundColor Cyan

  Assert-That 'git push is DENIED with no receipts' `
    (Invoke-Gate 'git push -u origin feature')

  Assert-That 'gh pr create is DENIED with no receipts' `
    (Invoke-Gate 'gh pr create --fill')

  Assert-That 'git -C <path> push is DENIED too' `
    (Invoke-Gate 'git -C . push')

  Write-Host ''
  Write-Host 'Releasing: recorded reviews unblock the push' -ForegroundColor Cyan

  'code-review: no findings' | & pwsh -NoProfile -File $writer -Kind code-review | Out-Null
  Assert-That 'still DENIED with only one of the two reviews' `
    (Invoke-Gate 'git push')

  'simplify: no findings' | & pwsh -NoProfile -File $writer -Kind simplify | Out-Null
  Assert-That 'ALLOWED once both reviews are recorded' `
    (-not (Invoke-Gate 'git push'))

  Write-Host ''
  Write-Host 'Economy: a non-source commit must not discard the reviews' -ForegroundColor Cyan

  Set-Content -Path (Join-Path $tmp 'README.md') -Value 'docs update' -Encoding UTF8
  & git -C $tmp add -A
  & git -C $tmp commit -q -m 'docs: update readme'

  Assert-That 'still ALLOWED after a docs-only commit' `
    (-not (Invoke-Gate 'git push'))

  Write-Host ''
  Write-Host 'Thoroughness: a source change must re-arm the gate' -ForegroundColor Cyan

  Set-Content -Path (Join-Path $tmp 'src/Content/Widget.cs') -Value 'public class Widget { public int N; public int M; }' -Encoding UTF8
  & git -C $tmp add -A
  & git -C $tmp commit -q -m 'feat: change source again'

  Assert-That 'DENIED again after source changes' `
    (Invoke-Gate 'git push')

  # A .tsx-only change must gate too: before 2026-08-02 it did not, so every React component
  # in both frontends could be pushed unreviewed.
  & git -C $tmp checkout -q -b tsx-only main
  New-Item -ItemType Directory -Force -Path (Join-Path $tmp 'src/Content/ui') | Out-Null
  Set-Content -Path (Join-Path $tmp 'src/Content/ui/Panel.tsx') -Value 'export const Panel = () => null;' -Encoding UTF8
  & git -C $tmp add -A
  & git -C $tmp commit -q -m 'feat: add a react component'

  Assert-That 'a .tsx-only change is DENIED without receipts' `
    (Invoke-Gate 'git push')

  Write-Host ''
  Write-Host 'Escape hatch' -ForegroundColor Cyan

  $env:RAILS_SKIP_REVIEW_GATE = '1'
  Assert-That 'RAILS_SKIP_REVIEW_GATE=1 allows the push' `
    (-not (Invoke-Gate 'git push'))
  $env:RAILS_SKIP_REVIEW_GATE = $null

  Write-Host ''
  Write-Host 'Uncommitted work' -ForegroundColor Cyan

  & git -C $tmp checkout -q feature
  Set-Content -Path (Join-Path $tmp 'src/Content/Widget.cs') -Value 'public class Widget { public int Uncommitted; }' -Encoding UTF8
  Assert-That 'a dirty src/ tree is DENIED' `
    (Invoke-Gate 'git push')

} finally {
  $env:CLAUDE_PROJECT_DIR = $originalProjectDir
  $env:RAILS_SKIP_REVIEW_GATE = $originalSkip
  Remove-Item -Recurse -Force -Path $tmp -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:Failures -gt 0) {
  Write-Host "$script:Failures of $script:Ran assertions FAILED" -ForegroundColor Red
  exit 1
}
Write-Host "All $script:Ran assertions passed" -ForegroundColor Green
exit 0
