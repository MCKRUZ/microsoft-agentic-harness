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
#   1. Posting a tier-2 sign-off (`Reviewed-by: @<owner> <sha> — ...`). tier-gate.yml turns
#      that comment into the green `tier-gate` status a tier-2 PR needs; only Matt may write it.
#   2. Applying an `accepted-risk:*` label. Those labels override a blocking correctness or
#      security finding; accepting a known risk is a human decision.
#   3. Re-arming the tier-0 circuit breaker: closing, or removing the label from, an issue
#      labelled `rails:breaker`. The breaker trips when a tier-0 auto-merge was reverted; Matt
#      reads why before turning auto-merge back on.
#   4. Merging any PR that is not labelled `tier:0`, and adding or removing a `tier:*` label
#      (tier-gate.yml computes it from the change; a hand-set label would steer this very
#      check). Tier 0 (plain docs, additive tests) merges
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

# Any login: a team cloning this template has its own owner, and tier-status.mjs reads the
# repository owner, so the guard must not assume MCKRUZ.
$SignoffPattern  = '(?i)Reviewed-by:\s*@?[A-Za-z0-9]'
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

function Get-Labels([string]$kind, [string]$number, [string]$repo = '') {
  # Returns the label names on an issue or PR, or $null when the lookup fails.
  $ghArgs = @($kind, 'view')
  if ($number) { $ghArgs += $number }
  if ($repo) { $ghArgs += @('--repo', $repo) }
  $ghArgs += @('--json', 'labels', '--jq', '.labels[].name')
  $out = & gh @ghArgs 2>$null
  if ($LASTEXITCODE -ne 0) { return $null }
  return @($out | Where-Object { $_ })
}

function Get-GhTarget([string]$segment, [string]$verbPattern, [string[]]$valuedFlags) {
  # Parses one shell segment like `gh pr merge --squash --admin 950 --repo o/r` into its
  # positional target(s), skipping flags and the values of flags that take one. The target can
  # come before or after the flags; reading only the token straight after the verb (the first
  # version of this guard did) let `gh pr merge --admin 950` be checked against the wrong PR.
  $m = [regex]::Match($segment, "(?i)\bgh\s+$verbPattern\b(.*)$")
  if (-not $m.Success) { return $null }
  $tokens = @([regex]::Matches($m.Groups[$m.Groups.Count - 1].Value, '"[^"]*"|''[^'']*''|\S+') | ForEach-Object { $_.Value.Trim('"', "'") })
  $positionals = @()
  $repo = ''
  for ($i = 0; $i -lt $tokens.Count; $i++) {
    $t = $tokens[$i]
    if ($t -in '-R', '--repo') { $repo = if ($i + 1 -lt $tokens.Count) { $tokens[$i + 1] } else { '' }; $i++; continue }
    if ($t -like '--repo=*') { $repo = $t.Substring(7); continue }
    if ($t.StartsWith('-')) {
      if ($t -notmatch '=' -and $valuedFlags -contains $t) { $i++ }
      continue
    }
    $positionals += $t
  }
  return [pscustomobject]@{ Target = ($positionals | Select-Object -First 1); Ambiguous = ($positionals.Count -gt 1); Repo = $repo }
}

function Test-Merge([string]$number, [string]$repo = '') {
  $labels = Get-Labels 'pr' $number $repo
  $which = if ($number) { "PR #$number" } else { 'the current branch''s PR' }
  if ($null -eq $labels) {
    Deny "could not read the labels of $which, so it cannot be shown to be tier 0. Only tier-0 PRs are merged by the agent; ask Matt to merge it."
  }
  $tiers = @($labels | Where-Object { $_ -match '^tier:' })
  if ($tiers.Count -eq 1 -and $tiers[0] -eq 'tier:0') { Allow }
  $found = if ($tiers.Count) { $tiers -join ', ' } else { 'no tier label' }
  Deny "$which is not tier 0 ($found). Tier-1 and tier-2 PRs are merged by Matt after he reads the evidence block. Tell him it is ready and stop."
}

function Test-BreakerIssue([string]$number, [string]$repo = '') {
  $labels = Get-Labels 'issue' $number $repo
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
  if ($command -match '(?i)\btier:[0-2]\b' -and $command -match '(?i)(--add-label|--remove-label|/labels\b|label(s)?\s*=)' -and $command -notmatch '(?i)\bgh\s+label\s+(create|list|view|edit)\b') {
    Deny 'tier labels are computed from the change by tier-gate.yml, never set by hand. If the tier looks wrong, tell Matt why; the fix belongs in .github/scripts/tier-rules.mjs.'
  }
  if ($command -match '(?i)rails:breaker' -and $command -match '(?i)(--remove-label|\bclose\b|state\s*=\s*closed)') {
    Deny 'that would re-arm the tier-0 circuit breaker (rails:breaker). Only Matt re-arms it.'
  }
  # The tier-gate status is what branch protection reads. Only tier-gate.yml may set it; a
  # status or check run posted with the agent's (= Matt's) token would forge the sign-off.
  if ($command -match '(?i)/(statuses|check-runs)\b' -and $command -match '(?i)(-X\s*POST|--method\s+POST|\s-f\s|\s-F\s|--field|--raw-field|--input)') {
    Deny 'posting a commit status or check run by hand would stand in for a rail (tier-gate, a reviewer). Only the workflows publish those.'
  }

  foreach ($seg in ($command -split '\|\||&&|[;|&]')) {
    $issue = Get-GhTarget $seg 'issue\s+(close|edit|reopen)' @('-c', '--comment', '-r', '--reason', '-t', '--title', '-b', '--body', '-F', '--body-file', '-m', '--milestone', '--add-assignee', '--remove-assignee', '--add-label', '--remove-label', '--add-project', '--remove-project')
    if ($issue) {
      $closing = $seg -match '(?i)\bissue\s+close\b' -or $seg -match '(?i)--remove-label'
      if ($closing) {
        if ($issue.Ambiguous -or -not $issue.Target) { Deny 'could not tell which issue this closes or relabels, so it cannot be ruled out as the rails:breaker issue. Name exactly one issue.' }
        Test-BreakerIssue ($issue.Target -replace '^.*/issues/', '' -replace '^#', '') $issue.Repo
      }
    }
    $merge = Get-GhTarget $seg 'pr\s+merge' @('-b', '--body', '-F', '--body-file', '-t', '--subject', '--match-head-commit', '-A', '--author-email')
    if ($merge) {
      if ($merge.Ambiguous) { Deny 'this merge names more than one PR. Merge one tier-0 PR at a time.' }
      Test-Merge ($merge.Target -replace '^.*/pull/', '' -replace '^#', '') $merge.Repo
    }
  }
  $m = [regex]::Match($command, '(?i)repos/([^/\s]+/[^/\s]+)/issues/(\d+)\b[^|;&]*state\W+closed')
  if ($m.Success) { Test-BreakerIssue $m.Groups[2].Value $m.Groups[1].Value }
  $m = [regex]::Match($command, '(?i)repos/([^/\s]+/[^/\s]+)/pulls/(\d+)/merge\b')
  if ($m.Success) { Test-Merge $m.Groups[2].Value $m.Groups[1].Value }
  Allow
}

if ($tool -in 'Write', 'Edit', 'MultiEdit') {
  # A workflow that publishes `tier-gate`, or that may write commit statuses, could forge the
  # gate from inside a PR. Only tier-gate.yml does that, and it runs from the default branch.
  $path = ([string]$toolInput.file_path) -replace '\\', '/'
  if ($path -match '(^|/)\.github/workflows/[^/]+$' -and $path -notmatch '/tier-gate\.ya?ml$') {
    $text = @($toolInput.content, $toolInput.new_string) + @($toolInput.edits | ForEach-Object { $_.new_string }) -join "`n"
    if ($text -match '(?i)statuses\s*:\s*write|/statuses/|context[=:\s]+[''"]?tier-gate') {
      Deny 'only tier-gate.yml may publish the tier-gate status or hold statuses: write; any other workflow could forge the tier-2 sign-off from inside a PR. If a rail genuinely needs it, Matt makes that change.'
    }
  }
  Allow
}

if ($tool -like 'mcp__github__*') {
  $text = $toolInput | ConvertTo-Json -Depth 10 -Compress
  if ($text -match $SignoffPattern) {
    Deny 'a "Reviewed-by: @MCKRUZ" sign-off is Matt''s own statement that he read a tier-2 change. The agent never posts it.'
  }
  if ($text -match '(?i)\btier:[0-2]\b' -and $text -match '(?i)"labels"') {
    Deny 'tier labels are computed from the change by tier-gate.yml, never set by hand.'
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
