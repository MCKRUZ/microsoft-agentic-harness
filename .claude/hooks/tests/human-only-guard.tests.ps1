#!/usr/bin/env pwsh
#
# human-only-guard.tests.ps1 — proof that the agent cannot make Matt's decisions through the
# ordinary paths: post a tier-2 sign-off, apply an accepted-risk label, re-arm the circuit
# breaker, or merge a PR that is not tier 0. Driven through the hook's real stdin contract, with
# a stub `gh` first on PATH so label lookups are deterministic and nothing reaches GitHub.
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

# --- stub gh -----------------------------------------------------------------------------------
$stubDir = Join-Path ([System.IO.Path]::GetTempPath()) ("hog-stub-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stubDir | Out-Null
$stubBody = @'
if ($env:STUB_FAIL -eq '1') { exit 1 }
$kind = $args[0]
$list = if ($kind -eq 'pr') { $env:STUB_PR_LABELS } else { $env:STUB_ISSUE_LABELS }
if ($list) { $list -split ',' | ForEach-Object { $_ } }
exit 0
'@
Set-Content -LiteralPath (Join-Path $stubDir 'gh.ps1') -Value $stubBody
$shStub = Join-Path $stubDir 'gh'
Set-Content -LiteralPath $shStub -Value "#!/usr/bin/env pwsh`n$stubBody"
if (-not $IsWindows) { & chmod +x $shStub }
$env:PATH = $stubDir + [System.IO.Path]::PathSeparator + $env:PATH

function Invoke-Guard([string]$ToolName, [hashtable]$ToolInput, [hashtable]$Env = @{}) {
  foreach ($k in 'STUB_PR_LABELS', 'STUB_ISSUE_LABELS', 'STUB_FAIL') { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
  foreach ($k in $Env.Keys) { Set-Item "Env:$k" $Env[$k] }
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
function Bash([string]$cmd, [hashtable]$Env = @{}) { Invoke-Guard 'Bash' @{ command = $cmd } $Env }

Write-Host 'Sign-off'
Assert-Decision 'agent posting a sign-off comment is denied' $true (Bash 'gh pr comment 12 --body "Reviewed-by: @MCKRUZ abc1234 — read it"')
Assert-Decision 'sign-off via gh api is denied' $true (Bash 'gh api repos/MCKRUZ/x/issues/12/comments -f body="Reviewed-by: MCKRUZ abc1234 - ok"')
$bodyFile = Join-Path $stubDir 'body.md'
Set-Content -LiteralPath $bodyFile -Value "Looks fine.`nReviewed-by: @MCKRUZ abc1234 — read the evidence"
Assert-Decision 'sign-off smuggled through --body-file is denied' $true (Bash "gh pr comment 12 --body-file `"$bodyFile`"")
Assert-Decision 'sign-off via the GitHub MCP tool is denied' $true (Invoke-Guard 'mcp__github__add_issue_comment' @{ body = 'Reviewed-by: @MCKRUZ abc1234 — ok'; issue_number = 12 })
Assert-Decision "a template consumer's owner sign-off is denied too" $true (Bash 'gh pr comment 12 --body "Reviewed-by: @acme-lead abc1234 — read it"')
Assert-Decision 'an ordinary PR comment is allowed' $false (Bash 'gh pr comment 12 --body "Ready for your sign-off."')
Assert-Decision 'grepping docs for the pattern (no GitHub write) is allowed' $false (Bash 'git grep -n "Reviewed-by: @MCKRUZ" .github/RAILS.md')

Write-Host 'Accepted-risk labels'
Assert-Decision 'adding accepted-risk:security is denied' $true (Bash 'gh pr edit 12 --add-label accepted-risk:security')
Assert-Decision 'adding accepted-risk:correctness via api is denied' $true (Bash 'gh api repos/MCKRUZ/x/issues/12/labels -f "labels[]=accepted-risk:correctness"')
Assert-Decision 'creating the label definition is allowed' $false (Bash 'gh label create accepted-risk:security --color B60205')
Assert-Decision 'adding an ordinary label is allowed' $false (Bash 'gh pr edit 12 --add-label needs-triage')

Write-Host 'Tier labels'
Assert-Decision 'relabelling a PR tier:0 by hand is denied' $true (Bash 'gh pr edit 12 --add-label tier:0')
Assert-Decision 'removing a tier label by hand is denied' $true (Bash 'gh pr edit 12 --remove-label tier:2')
Assert-Decision 'setting a tier label through the MCP tool is denied' $true (Invoke-Guard 'mcp__github__issue_write' @{ issue_number = 12; labels = @('tier:0') })
Assert-Decision 'creating the tier label definitions is allowed' $false (Bash 'gh label create tier:0 --color 0E8A16')

Write-Host 'Circuit breaker'
Assert-Decision 'closing the breaker issue is denied' $true (Bash 'gh issue close 40' @{ STUB_ISSUE_LABELS = 'rails:breaker' })
Assert-Decision 'removing the breaker label is denied' $true (Bash 'gh issue edit 40 --remove-label rails:breaker')
Assert-Decision 'closing an ordinary issue is allowed' $false (Bash 'gh issue close 41 --comment "fixed in #42"' @{ STUB_ISSUE_LABELS = 'bug' })
Assert-Decision 'closing an issue whose labels cannot be read is denied' $true (Bash 'gh issue close 41' @{ STUB_FAIL = '1' })

Write-Host 'Merging'
Assert-Decision 'merging a tier:0 PR is allowed' $false (Bash 'gh pr merge 12 --merge --admin' @{ STUB_PR_LABELS = 'tier:0,documentation' })
Assert-Decision 'merging a tier:1 PR is denied' $true (Bash 'gh pr merge 12 --merge' @{ STUB_PR_LABELS = 'tier:1' })
Assert-Decision 'merging a tier:2 PR is denied' $true (Bash 'gh pr merge 12 --squash --admin' @{ STUB_PR_LABELS = 'tier:2' })
Assert-Decision 'merging an unlabelled PR is denied' $true (Bash 'gh pr merge --merge' @{ STUB_PR_LABELS = '' })
Assert-Decision 'merging with two tier labels is denied' $true (Bash 'gh pr merge 12' @{ STUB_PR_LABELS = 'tier:0,tier:2' })
Assert-Decision 'merging when labels cannot be read is denied' $true (Bash 'gh pr merge 12' @{ STUB_FAIL = '1' })
Assert-Decision 'merging through the REST endpoint is gated too' $true (Bash 'gh api -X PUT repos/MCKRUZ/x/pulls/12/merge' @{ STUB_PR_LABELS = 'tier:1' })
Assert-Decision 'merging through the MCP tool is gated too' $true (Invoke-Guard 'mcp__github__merge_pull_request' @{ owner = 'MCKRUZ'; repo = 'x'; pullNumber = 12 } @{ STUB_PR_LABELS = 'tier:2' })
Assert-Decision 'viewing a PR is allowed' $false (Bash 'gh pr view 12 --json labels')
Assert-Decision 'git commands are untouched' $false (Bash 'git merge origin/main')

Remove-Item -LiteralPath $stubDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "$($script:Ran - $script:Failures)/$($script:Ran) passed"
if ($script:Failures -gt 0) { exit 1 }
