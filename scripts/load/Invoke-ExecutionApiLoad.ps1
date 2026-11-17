<#
.SYNOPSIS
    Runs the Execution API load scenarios (k6) against a local load target and collects the results.

.DESCRIPTION
    For each scenario this script starts a fresh Presentation.ExecutionApi.LoadTarget (the real
    Execution API on Kestrel, with a simulated model and locally minted tokens), waits until it is
    ready, records its runtime counters with dotnet-counters, runs the k6 scenario, and always stops
    the target afterwards. A fresh target per scenario keeps one scenario's queued runs, rate-limit
    windows and memory from colouring the next one's numbers.

    Results go to artifacts/load/<timestamp>/<scenario>/ (gitignored):
      report.html       k6 web-dashboard export
      summary.json      k6 end-of-test summary, for comparing runs
      k6.log            k6 console output, including the threshold verdicts
      target.log        the load target's console output
      counters.csv      thread pool, GC, allocation, Kestrel and rate-limiter counters, once a second

    Machine protection: the script refuses to start with less than 6 GB of free memory, puts a hard
    time limit on every scenario, and the heavy profiles (stress, soak) need -ConfirmHeavy.

    Prerequisites (checked at start):
      k6               winget install k6 --source winget
      dotnet-counters  dotnet tool install -g dotnet-counters   (optional; counters are skipped without it)

.PARAMETER LoadProfile
    smoke (default, about a minute per scenario), baseline (the reference measurement), stress (ramps
    far past baseline to find where the host bends), or soak (an hour of steady mixed load; runs only
    the soak scenario).

.PARAMETER Scenario
    Scenario names to run (file names under k6/scenarios without .js). Defaults to every scenario that
    suits the profile.

.PARAMETER Model
    Simulated model profile: instant, realistic (default), slow or flaky.

.PARAMETER FailureRate
    Overrides the model's transient-failure rate (0 to 1). Set to 1 to prove the thresholds can fail.

.PARAMETER ConfirmHeavy
    Required for the stress and soak profiles.

.PARAMETER EnableToolInvocation
    Starts the target with direct tool invocation on and adds the tool-invoke scenario.

.PARAMETER WithTelemetry
    Leaves the target's OTLP export on, so a run measures the cost of telemetry too.

.EXAMPLE
    ./scripts/load/Invoke-ExecutionApiLoad.ps1

.EXAMPLE
    ./scripts/load/Invoke-ExecutionApiLoad.ps1 -Profile baseline -Scenario stream-soak,submit-burst
#>
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'baseline', 'stress', 'soak')]
    [Alias('Profile')]
    [string] $LoadProfile = 'smoke',

    [string[]] $Scenario,

    [ValidateSet('instant', 'realistic', 'slow', 'flaky')]
    [string] $Model = 'realistic',

    [ValidateRange(0.0, 1.0)]
    [Nullable[double]] $FailureRate,

    [ValidateRange(1024, 65535)]
    [int] $Port = 5080,

    [switch] $ConfirmHeavy,
    [switch] $EnableToolInvocation,
    [switch] $WithTelemetry
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$TargetProject = Join-Path $RepoRoot 'src\Content\Tests\Presentation.ExecutionApi.LoadTarget'
$TargetDll = Join-Path $TargetProject 'bin\Release\net10.0\Presentation.ExecutionApi.LoadTarget.dll'
$ScenarioDir = Join-Path $TargetProject 'k6\scenarios'

# Enough callers for every scenario at every profile: the stress ramp (400 VUs) plus the spare callers
# limits-hold and noisy-neighbour single out. Minting them takes well under a second.
$Callers = 405
$MinimumFreeMemoryGb = 6
$ReadySignal = 'LOAD TARGET READY'
$ScenarioTimeoutMinutes = @{ smoke = 10; baseline = 20; stress = 30; soak = 90 }
$Counters = 'System.Runtime,Microsoft.AspNetCore.Hosting,Microsoft.AspNetCore.Server.Kestrel,Microsoft.AspNetCore.RateLimiting'

function Get-DefaultScenarios {
    if ($LoadProfile -eq 'soak') { return @('soak') }
    $names = @('poll-storm', 'submit-burst', 'stream-soak', 'register-burst', 'workflow-runs',
        'noisy-neighbour', 'limits-hold', 'spike')
    if ($EnableToolInvocation) { $names += 'tool-invoke' }
    return $names
}

# winget adds k6 to PATH for new shells only; fall back to its install folder so a run straight after
# installing works without restarting the terminal.
function Resolve-K6 {
    $onPath = Get-Command k6 -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $installed = Join-Path $env:ProgramFiles 'k6\k6.exe'
    if (Test-Path $installed) { return $installed }
    throw 'k6 is not installed. Install it with: winget install k6 --source winget'
}

function Assert-Prerequisites {
    if (-not (Get-Command dotnet-counters -ErrorAction SilentlyContinue)) {
        Write-Warning 'dotnet-counters is not installed; runtime counters will be skipped. Install: dotnet tool install -g dotnet-counters'
    }
    if ($LoadProfile -in 'stress', 'soak' -and -not $ConfirmHeavy) {
        throw "The $LoadProfile profile loads this machine heavily. Re-run with -ConfirmHeavy to proceed."
    }
    if ($Scenario -contains 'tool-invoke' -and -not $EnableToolInvocation) {
        throw 'The tool-invoke scenario needs -EnableToolInvocation.'
    }
    foreach ($name in $Scenario) {
        if (-not (Test-Path (Join-Path $ScenarioDir "$name.js"))) { throw "No scenario named '$name' in $ScenarioDir." }
    }
}

function Assert-FreeMemory {
    $freeGb = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
    if ($freeGb -lt $MinimumFreeMemoryGb) {
        throw ('Only {0:N1} GB of memory is free; the load run needs at least {1} GB. Close something and retry.' -f $freeGb, $MinimumFreeMemoryGb)
    }
    Write-Host ('Free memory: {0:N1} GB' -f $freeGb)
}

function Build-Target {
    Write-Host 'Building the load target (Release)...'
    & dotnet build (Join-Path $TargetProject 'Presentation.ExecutionApi.LoadTarget.csproj') -c Release --nologo -v quiet -m:4
    if ($LASTEXITCODE -ne 0) { throw "Building the load target failed (exit $LASTEXITCODE)." }
}

function Start-Target([string] $OutDir) {
    $fixtures = Join-Path $OutDir 'fixtures'
    $arguments = @("`"$TargetDll`"", '--out', "`"$fixtures`"", '--port', $Port, '--model', $Model, '--callers', $Callers)
    if ($null -ne $FailureRate) { $arguments += @('--failure-rate', $FailureRate.ToString([cultureinfo]::InvariantCulture)) }
    if ($EnableToolInvocation) { $arguments += '--enable-tool-invocation' }
    if ($WithTelemetry) { $arguments += '--with-telemetry' }

    $log = Join-Path $OutDir 'target.log'
    $process = Start-Process dotnet -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError (Join-Path $OutDir 'target.err.log')
    Wait-ForReady $process $log
    return [pscustomobject]@{ Process = $process; Fixtures = $fixtures }
}

function Wait-ForReady([System.Diagnostics.Process] $Process, [string] $Log) {
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { throw "The load target exited during start-up (exit $($Process.ExitCode)); see $Log." }
        if ((Test-Path $Log) -and (Select-String -Path $Log -Pattern $ReadySignal -SimpleMatch -Quiet)) { return }
        Start-Sleep -Milliseconds 500
    }
    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    throw "The load target did not report ready within 2 minutes; see $Log."
}

function Start-Counters([int] $ProcessId, [string] $OutDir) {
    if (-not (Get-Command dotnet-counters -ErrorAction SilentlyContinue)) { return $null }
    $arguments = @('collect', '--process-id', $ProcessId, '--refresh-interval', '1', '--format', 'csv',
        '--output', "`"$(Join-Path $OutDir 'counters.csv')`"", '--counters', $Counters)
    return Start-Process dotnet-counters -ArgumentList $arguments -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $OutDir 'counters.log') -RedirectStandardError (Join-Path $OutDir 'counters.err.log')
}

function Invoke-K6([string] $Name, [string] $Fixtures, [string] $OutDir) {
    # Scenario inputs go to k6 as -e values, so nothing is left behind in the caller's shell.
    $arguments = @('run', '--quiet', '-e', "FIXTURES=`"$Fixtures`"", '-e', "PROFILE=$LoadProfile",
        '--summary-export', "`"$(Join-Path $OutDir 'summary.json')`"", "`"$(Join-Path $ScenarioDir "$Name.js")`"")

    # The dashboard export is configured only through the environment; set it for k6's start, then restore.
    $saved = @{ K6_WEB_DASHBOARD = $env:K6_WEB_DASHBOARD; K6_WEB_DASHBOARD_EXPORT = $env:K6_WEB_DASHBOARD_EXPORT }
    try {
        $env:K6_WEB_DASHBOARD = 'true'
        $env:K6_WEB_DASHBOARD_EXPORT = Join-Path $OutDir 'report.html'
        $k6 = Start-Process $K6Path -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $OutDir 'k6.log') -RedirectStandardError (Join-Path $OutDir 'k6.err.log')
        # Without touching Handle now, ExitCode can read back empty once the process has exited.
        $null = $k6.Handle
    }
    finally {
        $env:K6_WEB_DASHBOARD = $saved.K6_WEB_DASHBOARD
        $env:K6_WEB_DASHBOARD_EXPORT = $saved.K6_WEB_DASHBOARD_EXPORT
    }

    $timeoutMs = $ScenarioTimeoutMinutes[$LoadProfile] * 60 * 1000
    if (-not $k6.WaitForExit($timeoutMs)) {
        Stop-Process -Id $k6.Id -Force -ErrorAction SilentlyContinue
        Write-Warning "$Name hit the $($ScenarioTimeoutMinutes[$LoadProfile])-minute limit and was stopped."
        return 'timed out'
    }
    # k6 exits 0 when every threshold passed and 99 when one failed; anything else is a script error.
    switch ($k6.ExitCode) { 0 { 'passed' } 99 { 'thresholds failed' } default { "k6 error (exit $($k6.ExitCode))" } }
}

function Stop-Target($Target, $CountersProcess) {
    if ($Target -and -not $Target.Process.HasExited) { Stop-Process -Id $Target.Process.Id -Force -ErrorAction SilentlyContinue }
    if ($CountersProcess) {
        # dotnet-counters ends on its own once the process it watches exits; give it a moment to flush.
        if (-not $CountersProcess.WaitForExit(10000)) { Stop-Process -Id $CountersProcess.Id -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-Scenario([string] $Name, [string] $RunDir) {
    $outDir = New-Item -ItemType Directory -Path (Join-Path $RunDir $Name) -Force
    Write-Host "`n== $Name ($LoadProfile, model $Model)"
    $target = $null
    $countersProcess = $null
    try {
        $target = Start-Target $outDir.FullName
        $countersProcess = Start-Counters $target.Process.Id $outDir.FullName
        $verdict = Invoke-K6 $Name $target.Fixtures $outDir.FullName
    }
    catch {
        $verdict = "error: $($_.Exception.Message)"
    }
    finally {
        Stop-Target $target $countersProcess
    }
    Write-Host "   $verdict  ->  $($outDir.FullName)"
    return [pscustomobject]@{ Scenario = $Name; Result = $verdict }
}

if (-not $Scenario) { $Scenario = Get-DefaultScenarios }
$K6Path = Resolve-K6
Assert-Prerequisites
Assert-FreeMemory
Build-Target

$runDir = Join-Path $RepoRoot ("artifacts\load\{0:yyyyMMdd-HHmmss}-$LoadProfile" -f (Get-Date))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$results = foreach ($name in $Scenario) { Invoke-Scenario $name $runDir }

Write-Host "`nResults: $runDir"
$results | Format-Table -AutoSize | Out-String | Write-Host
if ($results | Where-Object Result -ne 'passed') { exit 1 }
