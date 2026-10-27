#!/usr/bin/env pwsh
#
# human-only-guard.ps1 — keep the agent's hands off the decisions only Matt may make.
#
# The rails promise that "an agent's approval never counts as a human's" (Anthropic, "How the
# Claude Code team does code review"; .github/RAILS.md "Review conventions"). On this solo repo
# that promise is hard to keep mechanically: the agent's `gh` is authenticated AS Matt, so to
# GitHub an agent-posted sign-off, an agent-applied override label and an agent-clicked merge are
# indistinguishable from his. This hook is what makes the difference real for every action taken
# through Claude Code. It fires on PreToolUse for Bash and the GitHub MCP tools and denies:
#
#   1. Posting a tier-2 sign-off (`Reviewed-by: @MCKRUZ <sha> — ...`). tier-signoff.yml turns
#      that comment into the green `tier-gate` status a tier-2 PR needs; only Matt may write it.
#   2. Applying an `accepted-risk:*` label. Those labels override a blocking correctness or
#      security finding; accepting a known risk is a human decision.
#   3. Re-arming the tier-0 circuit breaker: closing, or removing the label from, an issue
#      labelled `rails:breaker`. The breaker trips when a tier-0 auto-merge was reverted; Matt
#      reads why before turning auto-merge back on.
#   4. Merging any PR that is not labelled `tier:0`. Tier 0 (plain docs, additive tests) merges
#      on green without a human read; tier 1 and tier 2 are merged by Matt, who reads the
#      evidence first. tier-gate.yml computes the label from the change, never from the author.
#
# Fail-closed: when a decision needs GitHub state (which labels an issue or PR carries) and the
# lookup fails, the action is denied with the reason, never allowed on a guess.
#
# Residual risk, stated plainly (do not describe this hook as stronger than it is): it inspects
# the tool call's own text, plus a file named by `--body-file`, `--input` or `-F x=@file`. An agent
# determined to evade it could still build the request some other way (a script it writes and
# runs, an encoded body). Matt pushing or commenting from his own terminal is not gated at all —
# nor should it be. What it does guarantee is that the ordinary paths an agent uses cannot make
# these decisions by accident or by habit.
#
# Contract: emit a PreToolUse hookSpecificOutput with permissionDecision "deny" to block; emit
# nothing (exit 0) to allow. Tests: .claude/hooks/tests/human-only-guard.tests.ps1

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Allow { exit 0 }
function Deny([string]$reason) {
  @{
    hookSpecificOutput = @{
      hookEventName            = 'PreToolUse'
      permissionDecision       = 'deny'
      permissionDecisionReason = "human-only-guard: $reason"
    }
  } | ConvertTo-Json -Compress -Depth 5
  exit 0
}

$SignoffPattern  = '(?i)Reviewed-by:\s*@?MCKRUZ\b'
$GitHubWriteVerb = '(?i)(\bgh\b|\bcurl\b|Invoke-RestMethod|Invoke-WebRequest|api\.github\.com)'

function Get-ReferencedFileText([string]$command) {
  # Body text that reaches GitHub through a file rather than the command line.
  $paths = [System.Collections.Generic.List[string]]::new()
  foreach ($m in [regex]::Matches($command, '(?:--body-file|--input)\s+("([^"]+)"|''([^'']+)''|(\S+))')) {
    $paths.Add(@($m.Groups[2].Value, $m.Groups[3].Value, $m.Groups[4].Value | Where-Object { $_ })[0])
  }
  foreach ($m in [regex]::Matches($command, '-F\s+\w+=@("([^"]+)"|(\S+))')) {
    $paths.Add(@($m.Groups[2].Value, $m.Groups[3].Value | Where-Object { $_ })[0])
  }
  $text = ''
  foreach ($p in $paths) {
    if ($p -and $p -ne '-' -and (Test-Path -LiteralPath $p -PathType Leaf)) {
      $text += "`n" + (Get-Content -LiteralPath $p -Raw -ErrorAction SilentlyContinue)
    }
  }
  return $text
}

function Get-Labels([string]$kind, [string]$number) {
  # Returns the label names on an issue or PR, or $null when the lookup fails.
  $ghArgs = @($kind, 'view')
  if ($number) { $ghArgs += $number }
  $ghArgs += @('--json', 'labels', '--jq', '.labels[].name')
  $out = & gh @ghArgs 2>$null
  if ($LASTEXITCODE -ne 0) { return $null }
  return @($out | Where-Object { $_ })
}

function Test-Merge([string]$number) {
  $labels = Get-Labels 'pr' $number
  $which = if ($number) { "PR #$number" } else { 'the current branch''s PR' }
  if ($null -eq $labels) {
    Deny "could not read the labels of $which, so it cannot be shown to be tier 0. Only tier-0 PRs are merged by the agent; ask Matt to merge it."
  }
  $tiers = @($labels | Where-Object { $_ -match '^tier:' })
  if ($tiers.Count -eq 1 -and $tiers[0] -eq 'tier:0') { Allow }
  $found = if ($tiers.Count) { $tiers -join ', ' } else { 'no tier label' }
  Deny "$which is not tier 0 ($found). Tier-1 and tier-2 PRs are merged by Matt after he reads the evidence block. Tell him it is ready and stop."
}

function Test-BreakerIssue([string]$number) {
  $labels = Get-Labels 'issue' $number
  if ($null -eq $labels) {
    Deny "could not read the labels of issue #$number to rule out the rails:breaker issue. Close it from your own terminal or the GitHub UI if it is not the breaker."
  }
  if ($labels -contains 'rails:breaker') {
    Deny "issue #$number is the tier-0 circuit breaker (rails:breaker). Only Matt re-arms auto-merge, after reading why it tripped."
  }
}

try {
  $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
} catch {
  Allow  # not a payload we understand; the other gates still apply
}
$tool = [string]$payload.tool_name
$toolInput = $payload.tool_input

if ($tool -eq 'Bash') {
  $command = [string]$toolInput.command
  if (-not $command) { Allow }
  if ($command -notmatch $GitHubWriteVerb) { Allow }
  $text = $command + (Get-ReferencedFileText $command)

  if ($text -match $SignoffPattern) {
    Deny 'a "Reviewed-by: @MCKRUZ" sign-off is Matt''s own statement that he read a tier-2 change. The agent never posts it, not even on request. Tell Matt the PR is ready for his sign-off.'
  }
  if ($text -match '(?i)accepted-risk:' -and $command -match '(?i)(--add-label|/labels\b|label(s)?\s*=)' -and $command -notmatch '(?i)\bgh\s+label\s+(create|list|view|edit)\b') {
    Deny 'applying an accepted-risk:* label overrides a blocking review finding. Accepting a known risk is Matt''s decision; surface the finding to him instead.'
  }
  if ($command -match '(?i)rails:breaker' -and $command -match '(?i)(--remove-label|\bclose\b|state\s*=\s*closed)') {
    Deny 'that would re-arm the tier-0 circuit breaker (rails:breaker). Only Matt re-arms it.'
  }
  $m = [regex]::Match($command, '(?i)\bgh\s+issue\s+(close|edit)\s+(\d+)')
  if ($m.Success -and ($m.Groups[1].Value -ieq 'close' -or $command -match '(?i)--remove-label')) {
    Test-BreakerIssue $m.Groups[2].Value
  }
  $m = [regex]::Match($command, '(?i)repos/[^/\s]+/[^/\s]+/issues/(\d+)\b[^|;&]*state\W+closed')
  if ($m.Success) { Test-BreakerIssue $m.Groups[1].Value }

  $m = [regex]::Match($command, '(?i)\bgh\s+pr\s+merge\b(?:\s+(?!-)(\S+))?')
  if ($m.Success) {
    $target = $m.Groups[1].Value -replace '^.*/pull/', '' -replace '^#', ''
    Test-Merge $target
  }
  $m = [regex]::Match($command, '(?i)repos/[^/\s]+/[^/\s]+/pulls/(\d+)/merge\b')
  if ($m.Success) { Test-Merge $m.Groups[1].Value }
  Allow
}

if ($tool -like 'mcp__github__*') {
  $text = $toolInput | ConvertTo-Json -Depth 10 -Compress
  if ($text -match $SignoffPattern) {
    Deny 'a "Reviewed-by: @MCKRUZ" sign-off is Matt''s own statement that he read a tier-2 change. The agent never posts it.'
  }
  if ($text -match '(?i)accepted-risk:' -and $text -match '(?i)"labels"') {
    Deny 'applying an accepted-risk:* label is Matt''s decision; surface the finding to him instead.'
  }
  if ($tool -eq 'mcp__github__merge_pull_request') {
    Test-Merge ([string]$toolInput.pullNumber)
  }
  if ($tool -eq 'mcp__github__issue_write' -and [string]$toolInput.state -eq 'closed' -and $toolInput.issue_number) {
    Test-BreakerIssue ([string]$toolInput.issue_number)
  }
  Allow
}

Allow
