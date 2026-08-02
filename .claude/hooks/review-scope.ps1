#!/usr/bin/env pwsh
#
# review-scope.ps1 — the single source of truth for "which code is under review, and what
# exact content did the reviewer see". Dot-sourced by review-gate.ps1 (which enforces) and
# save-review-receipt.ps1 (which records), so the two can never disagree about scope. When
# the enforcer and the recorder each carry their own copy of this logic, they drift, and a
# drifted gate silently passes unreviewed code.
#
# Why a content fingerprint instead of the commit SHA
# ---------------------------------------------------
# Receipts used to be named after the HEAD short-SHA. That binds review evidence to the
# commit rather than to the code, and the two diverge as soon as a branch gains a commit
# that touches no source. Measured on this repo (2026-08-02): of the 7 commits that carried
# review receipts, 6 changed no C# whatsoever — 4 docs-only commits on PR #224 and 2
# .github-only commits on PR #220. Every one of them invalidated both receipts and forced a
# full re-review of byte-identical source. That was ~12 of 14 review passes spent re-reading
# code that had not changed.
#
# Fingerprinting the reviewable diff fixes precisely that and nothing else:
#   * a commit that leaves the reviewable source identical yields the SAME fingerprint, so
#     the existing receipt still applies and no re-review is demanded;
#   * ANY change to reviewable source yields a DIFFERENT fingerprint, so the gate re-arms
#     exactly as it did before. Thoroughness is unchanged; only false alarms are removed.
#
# The fingerprint is taken over the diff TEXT (not just file names) so that reverting an
# edit, or changing a file and changing it back, is correctly recognised as "same code".

$ErrorActionPreference = 'Stop'
# Read native exit codes explicitly; a non-zero git call must not throw before the caller
# has had a chance to decide whether that means "allow" or "block".
$PSNativeCommandUseErrorActionPreference = $false

# Reviewable = first-party compilable source under src/. Deliberate inclusions/exclusions:
#   * tsx/jsx: 175 tracked .tsx files exist across Presentation.WebUI and
#     Presentation.Dashboard and were previously NOT matched, so every React component in
#     both frontends escaped the gate entirely. jsx is listed alongside it because leaving
#     it out would be an arbitrary gap in the same file class, not because any exist today.
#   * js: only the two eslint.config.js files are first-party today. Included so lint-rule
#     changes — which govern the quality bar itself — are reviewed.
#   * json is EXCLUDED on purpose. package-lock.json churns by hundreds of lines on every
#     dependency bump, and gating on it would reintroduce the exact "re-review unchanged
#     C# because a non-source file moved" cost this script exists to remove.
#   * node_modules/bin/obj are gitignored, so they can never appear in a git diff and need
#     no explicit exclusion here.
$script:ReviewablePattern = '^src/.*\.(cs|csproj|slnx|props|targets|razor|cshtml|ts|tsx|js|jsx|html)$'

function Resolve-ReviewBase {
  <#
  .SYNOPSIS
    Resolve the ref to diff against: origin/main, else main, else $null.
  .OUTPUTS
    The ref name, or $null when neither resolves (caller decides how to degrade).
  #>
  foreach ($ref in @('origin/main', 'main')) {
    $resolved = & git rev-parse --verify --quiet $ref 2>$null
    if ($LASTEXITCODE -eq 0 -and $resolved) { return $ref }
  }
  return $null
}

function Get-ReviewableChange {
  <#
  .SYNOPSIS
    The reviewable source changed between $Base and $Head, as paths plus the diff text.
  .DESCRIPTION
    Uses three-dot (merge-base) diff semantics so that main moving forward underneath an
    in-flight branch does NOT change the answer — only the branch's own commits do.
  .OUTPUTS
    A hashtable @{ Paths = string[]; Diff = string }, or $null when nothing reviewable
    changed. Throws only if git itself is missing, which callers guard for.
  #>
  param(
    [string]$Base,
    [string]$Head = 'HEAD'
  )

  if ($Base) {
    $changed = & git diff --name-only "$Base...$Head" 2>$null
  } else {
    # No base resolved (unusual: shallow clone, unborn main). Fall back to the tip commit's
    # own files so the gate can still scope on src/ rather than failing silently open.
    $changed = & git show --name-only --pretty=format: $Head 2>$null
  }
  if ($LASTEXITCODE -ne 0) { return $null }

  $paths = @($changed | Where-Object { $_ -match $script:ReviewablePattern })
  if ($paths.Count -eq 0) { return $null }

  if ($Base) {
    $diff = & git diff "$Base...$Head" -- $paths 2>$null
  } else {
    $diff = & git show --format= $Head -- $paths 2>$null
  }
  if ($LASTEXITCODE -ne 0) { return $null }

  return @{
    Paths = $paths
    Diff  = ($diff -join "`n")
  }
}

function Get-ReviewFingerprint {
  <#
  .SYNOPSIS
    A short, stable hash identifying exactly the reviewable code a reviewer would read.
  .DESCRIPTION
    Two commits whose reviewable source diff is byte-identical share a fingerprint, so a
    receipt recorded against one satisfies the other. Any difference in that diff — a real
    code edit — produces a different fingerprint and re-arms the gate.
  .OUTPUTS
    16 hex characters, or $null when nothing reviewable changed.
  #>
  param(
    [Parameter(Mandatory = $true)]
    [AllowNull()]
    $Change
  )

  if (-not $Change) { return $null }

  $bytes = [System.Text.Encoding]::UTF8.GetBytes($Change.Diff)
  $sha256 = [System.Security.Cryptography.SHA256]::Create()
  try {
    $hash = $sha256.ComputeHash($bytes)
  } finally {
    $sha256.Dispose()
  }
  # 8 bytes / 16 hex chars: collision risk is negligible for the handful of receipts a
  # single clone ever holds, and short names keep the receipt directory readable.
  return (-join ($hash[0..7] | ForEach-Object { $_.ToString('x2') }))
}
