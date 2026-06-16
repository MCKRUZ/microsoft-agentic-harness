#!/usr/bin/env pwsh
#
# stop-build-gate.ps1 — the methodology's "Stop gate" (the-rails.md §1, corollary
# "Mechanical self-validation is mandatory").
#
# Fires on the Claude Code `Stop` event. Before the agent is allowed to end its
# turn, this proves the build is green from the environment itself — not the
# agent's opinion that it is. A red build blocks the stop and hands the failure
# back to the agent to fix.
#
# Scope decisions (deliberate, documented):
#   * Build only by default. A full `dotnet test` on every Stop would cost
#     minutes per turn. Set RAILS_STOP_RUN_TESTS=1 to also run the suite.
#   * Only fires when there are uncommitted changes under src/. Doc-only or
#     planning turns don't pay for a build.
#   * Honors `stop_hook_active` to avoid an infinite stop->block->stop loop.
#   * Never wedges the session on missing tooling: if dotnet is absent it warns
#     and allows the stop rather than trapping the agent.
#
# Contract: emit {"decision":"block","reason":"..."} on stdout to block the stop;
# emit nothing (exit 0) to allow it.

$ErrorActionPreference = 'Stop'

# Do NOT let a native command's non-zero exit throw a terminating error: when
# $PSNativeCommandUseErrorActionPreference is $true (a PowerShell 7.4 default on
# some hosts), `dotnet build` on a red build would throw before we read its exit
# code, the script would die with no decision JSON, and Claude Code would treat a
# non-2 exit as non-blocking — letting the agent finish on a broken build. We
# read $LASTEXITCODE explicitly instead, so this gate fails CLOSED, not open.
$PSNativeCommandUseErrorActionPreference = $false

function Allow { exit 0 }
function Block([string]$reason) {
  @{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress
  exit 0
}

# --- Read the Stop event payload from stdin.
$raw = [Console]::In.ReadToEnd()
$payload = $null
if ($raw) { try { $payload = $raw | ConvertFrom-Json } catch { } }

# Already inside a stop-hook loop — never block again, or the agent can never finish.
if ($payload -and $payload.stop_hook_active) { Allow }

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location $projectDir

$solution = Join-Path $projectDir 'src/AgenticHarness.slnx'
if (-not (Test-Path $solution)) { Allow }   # nothing to build here

# --- Only gate when COMPILABLE source changed this session. Doc/json/asset edits
#     under src/ shouldn't trigger a full solution build every turn.
$dirtySrc = & git status --porcelain -- '*.cs' '*.csproj' '*.slnx' '*.props' '*.targets' '*.razor' '*.cshtml' 2>$null
if (-not $dirtySrc) { Allow }

# --- Tooling guard: don't trap the agent if dotnet isn't installed.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  [Console]::Error.WriteLine('stop-build-gate: dotnet not found; skipping build gate.')
  Allow
}

# --- Prove the build.
$buildOut = & dotnet build $solution --nologo --configuration Debug -clp:ErrorsOnly 2>&1
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) {
  $errLines = ($buildOut | Select-String -Pattern 'error' | Select-Object -First 15) -join "`n"
  Block("Build is red — you cannot finish on a broken build (delivery-rails Stop gate). " +
        "Fix the build before ending the turn. First errors:`n$errLines")
}

# --- Optionally prove the tests.
if ($env:RAILS_STOP_RUN_TESTS -eq '1') {
  $testOut = & dotnet test $solution --nologo --no-build --configuration Debug 2>&1
  if ($LASTEXITCODE -ne 0) {
    $failLines = ($testOut | Select-String -Pattern 'Failed|error' | Select-Object -First 15) -join "`n"
    Block("Tests are red — you cannot finish with failing tests (delivery-rails Stop gate). " +
          "Fix them before ending the turn. Summary:`n$failLines")
  }
}

Allow
