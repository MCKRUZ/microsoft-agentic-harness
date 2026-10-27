#!/usr/bin/env pwsh
#
# human-only-guard.tests.ps1 — proof that the agent cannot apply an accepted-risk override or post
# a check result by hand through the ordinary paths, driven through the hook's real stdin contract.
#
# Every deny case is paired with the nearest legitimate action that must stay allowed, because a
# guard that blocks ordinary work gets switched off.
#
# Run:  pwsh -NoProfile -File .claude/hooks/tests/human-only-guard.tests.ps1

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$guard = Join-Path (Split-Path -Parent $PSScriptRoot) 'human-only-guard.ps1'
$script:Failures = 0
$script:Ran = 0

function Invoke-Guard([string]$ToolName, [hashtable]$ToolInput) {
  $payload = @{ tool_name = $ToolName; tool_input = $ToolInput } | ConvertTo-Json -Compress -Depth 5
  $out = $payload | pwsh -NoProfile -File $guard
  return [bool]($out -match '"permissionDecision":"deny"')
}
function Assert-Decision([string]$Name, [bool]$ExpectDeny, [bool]$Denied) {
  $script:Ran++
  if ($Denied -eq $ExpectDeny) {
    Write-Host "  PASS  $Name" -ForegroundColor Green
  } else {
    $script:Failures++
    $want = if ($ExpectDeny) { 'deny' } else { 'allow' }
    Write-Host "  FAIL  $Name (expected $want)" -ForegroundColor Red
  }
}
function Bash([string]$cmd) { Invoke-Guard 'Bash' @{ command = $cmd } }

Write-Host 'Accepted-risk labels'
Assert-Decision 'adding accepted-risk:security is denied' $true (Bash 'gh pr edit 12 --add-label accepted-risk:security')
Assert-Decision 'adding accepted-risk:correctness via api is denied' $true (Bash 'gh api repos/MCKRUZ/x/issues/12/labels -f "labels[]=accepted-risk:correctness"')
Assert-Decision 'adding it through the MCP tool is denied' $true (Invoke-Guard 'mcp__github__issue_write' @{ issue_number = 12; labels = @('accepted-risk:security') })
Assert-Decision 'creating the label definition is allowed' $false (Bash 'gh label create accepted-risk:security --color B60205')
Assert-Decision 'adding an ordinary label is allowed' $false (Bash 'gh pr edit 12 --add-label needs-triage')

Write-Host 'Forging a check result'
Assert-Decision 'posting a commit status by hand is denied' $true (Bash 'gh api repos/MCKRUZ/x/statuses/abc1234 -f context=security-review -f state=success')
Assert-Decision 'posting a check run by hand is denied' $true (Bash 'gh api -X POST repos/MCKRUZ/x/check-runs --input run.json')
Assert-Decision 'reading statuses is allowed' $false (Bash 'gh api repos/MCKRUZ/x/commits/abc1234/statuses')

Write-Host 'Ordinary work'
Assert-Decision 'an ordinary PR comment is allowed' $false (Bash 'gh pr comment 12 --body "Ready for review."')
Assert-Decision 'merging is not this hook''s business (yet)' $false (Bash 'gh pr merge 12 --merge --admin')
Assert-Decision 'git commands are untouched' $false (Bash 'git merge origin/main')

Write-Host ''
Write-Host "$($script:Ran - $script:Failures)/$($script:Ran) passed"
if ($script:Failures -gt 0) { exit 1 }
