#!/usr/bin/env pwsh
#
# human-only-guard.ps1 — keep the agent's hands off the decisions only Matt may make.
#
# The rails promise that "an agent's approval never counts as a human's" (Anthropic, "How the
# Claude Code team does code review"; .github/RAILS.md "Review conventions"). On this solo repo
# that promise is hard to keep mechanically: the agent's `gh` is authenticated AS Matt, so to
# GitHub an agent-applied override label or an agent-posted check result is indistinguishable from
# his. This hook makes the difference real for every action taken through Claude Code. It fires on
# PreToolUse for Bash and the GitHub MCP tools and denies:
#
#   1. Applying an `accepted-risk:*` label. Those labels override a blocking correctness or
#      security finding; accepting a known risk is a human decision.
#   2. Posting a commit status or check run by hand. Required checks are published by the
#      workflows; one posted with Matt's token would stand in for a rail that never ran.
#
# The risk-tier rules that will add to this list (sign-offs, the tier-0 merge rule, the circuit
# breaker) are deliberately not here yet: they ship with the tier system itself.
#
# Residual risk, stated plainly (do not describe this hook as stronger than it is): it inspects
# the tool call's own text. An agent determined to evade it could still build the request some
# other way (a script it writes and runs, an encoded body). Matt acting from his own terminal is
# not gated at all — nor should it be. What it does guarantee is that the ordinary paths an agent
# uses cannot make these decisions by accident or by habit.
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

$GitHubWriteVerb = '(?i)(\bgh\b|\bcurl\b|Invoke-RestMethod|Invoke-WebRequest|api\.github\.com)'
$OverrideMessage = 'applying an accepted-risk:* label overrides a blocking review finding. Accepting a known risk is Matt''s decision; surface the finding to him instead.'

try {
  $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
} catch {
  Allow  # not a payload we understand; the other gates still apply
}
$tool = [string]$payload.tool_name
$toolInput = $payload.tool_input

if ($tool -eq 'Bash') {
  $command = [string]$toolInput.command
  if (-not $command -or $command -notmatch $GitHubWriteVerb) { Allow }

  if ($command -match '(?i)accepted-risk:' -and $command -match '(?i)(--add-label|/labels\b|label(s)?\s*=)' -and $command -notmatch '(?i)\bgh\s+label\s+(create|list|view|edit)\b') {
    Deny $OverrideMessage
  }
  if ($command -match '(?i)/(statuses|check-runs)\b' -and $command -match '(?i)(-X\s*POST|--method\s+POST|\s-f\s|\s-F\s|--field|--raw-field|--input)') {
    Deny 'posting a commit status or check run by hand would stand in for a rail that never ran. Only the workflows publish those.'
  }
  Allow
}

if ($tool -like 'mcp__github__*') {
  $text = $toolInput | ConvertTo-Json -Depth 10 -Compress
  if ($text -match '(?i)accepted-risk:' -and $text -match '(?i)"labels"') { Deny $OverrideMessage }
  Allow
}

Allow
