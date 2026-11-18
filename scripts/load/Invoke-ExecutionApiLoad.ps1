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

    Machine protection, all of it confined to the processes this script starts (nothing on the machine
    is changed outside them, and nothing outlives the run):
      - refuses to start while ComfyUI is running, or with less than 6 GB of free memory;
      - the build, the load target, k6 and dotnet-counters run at below-normal priority on at most two
        thirds of the cores, so the desktop keeps the rest;
      - a watchdog stops the whole run if free memory falls below 4 GB or the run passes its overall
        time limit (checked every 2 seconds), or if ComfyUI starts (checked every 15 seconds);
      - every scenario also has its own time limit;
      - the heavy profiles (stress, soak) need -ConfirmHeavy.

    Time limits (minutes)   whole run   each scenario
      smoke                     30            10
      baseline                  60            20
      stress                   120            30
      soak                      80            90 (one scenario; the run limit stops it first)

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
# The whole run, build included. Expected: smoke about 15 minutes, baseline about 40, stress about 90
# (each scenario waits up to 5 minutes for runs already started, and spike waits for its backlog to drain).
$RunTimeLimitMinutes = @{ smoke = 30; baseline = 60; stress = 120; soak = 80 }
$BuildTimeoutMinutes = 10
$AbortFreeMemoryGb = 4
$WatchIntervalMs = 2000
# Free memory is read every watchdog tick; the ComfyUI check is slower and changes rarely, so less often.
$ComfyUiCheckSeconds = 15
$ComfyUiPort = 8188
# The load processes share at most two thirds of the cores, so the desktop always keeps the rest. An
# affinity mask holds at most 64 cores, and shifting 1 left by 64 wraps to an empty mask, so cap at 63.
$LoadCores = [Math]::Min(63, [Math]::Max(2, [int][Math]::Floor([Environment]::ProcessorCount * 2 / 3)))
$LoadAffinity = [IntPtr](([long]1 -shl $LoadCores) - 1)
$script:RunDeadline = (Get-Date).AddMinutes($RunTimeLimitMinutes[$LoadProfile])
$script:AbortReason = $null
$script:K6Process = $null
$script:NextComfyUiCheck = [datetime]::MinValue

# Reading free memory through WMI every two seconds would put WMI work on the machine being measured;
# GlobalMemoryStatusEx is a single cheap system call.
Add-Type -Namespace LoadRun -Name Memory -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct Status { public uint Length; public uint Load; public ulong TotalPhys; public ulong AvailPhys;
    public ulong TotalPageFile; public ulong AvailPageFile; public ulong TotalVirtual; public ulong AvailVirtual;
    public ulong AvailExtendedVirtual; }
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GlobalMemoryStatusEx(ref Status status);
public static ulong AvailableBytes() {
    var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
    if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception();
    return status.AvailPhys;
}
'@
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

function Get-FreeMemoryGb { [LoadRun.Memory]::AvailableBytes() / 1GB }

function Assert-FreeMemory {
    $freeGb = Get-FreeMemoryGb
    if ($freeGb -lt $MinimumFreeMemoryGb) {
        throw ('Only {0:N1} GB of memory is free; the load run needs at least {1} GB. Close something and retry.' -f $freeGb, $MinimumFreeMemoryGb)
    }
    Write-Host ('Free memory: {0:N1} GB' -f $freeGb)
}

# ComfyUI holds GPU and system memory the load run would compete with. It is found by its port, or by a
# process started from a ComfyUI folder (the portable build runs a python.exe whose command line does not
# name ComfyUI). Matching the word "comfy" anywhere would also catch the comfy-mcp server, which is harmless.
function Get-ComfyUiEvidence {
    $listening = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object Port -eq $ComfyUiPort | Select-Object -First 1
    if ($listening) { return "something is listening on port $ComfyUiPort" }
    $process = Get-Process -Name python, ComfyUI -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -match '(?i)\\ComfyUI[^\\]*\\' } | Select-Object -First 1
    if ($process) { return "process $($process.Id) is running from $($process.Path)" }
    return $null
}

function Assert-ComfyUiStopped {
    $evidence = Get-ComfyUiEvidence
    if ($evidence) { throw "ComfyUI is running ($evidence). Stop it before a load run." }
}

# Anything that must stop the whole run, not only the current scenario.
function Get-RunAbortReason {
    if ((Get-Date) -ge $script:RunDeadline) { return "the run reached its $($RunTimeLimitMinutes[$LoadProfile])-minute limit" }
    $freeGb = Get-FreeMemoryGb
    if ($freeGb -lt $AbortFreeMemoryGb) { return ('free memory fell to {0:N1} GB' -f $freeGb) }
    if ((Get-Date) -ge $script:NextComfyUiCheck) {
        $script:NextComfyUiCheck = (Get-Date).AddSeconds($ComfyUiCheckSeconds)
        $comfy = Get-ComfyUiEvidence
        if ($comfy) { return "ComfyUI started ($comfy)" }
    }
    return $null
}

# Starts a process of this run at below-normal priority on the load cores. Environment values apply to this
# start only and are restored straight after, so nothing is left in the caller's shell.
function Start-LoadProcess([string] $FilePath, [string[]] $ArgumentList, [hashtable] $Redirect = @{}, [hashtable] $Environment = @{}) {
    $saved = @{}
    foreach ($name in $Environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $Environment[$name])
    }
    try {
        $process = Start-Process $FilePath -ArgumentList $ArgumentList -PassThru -NoNewWindow @Redirect
        # Without touching Handle now, ExitCode can read back empty once the process has exited.
        $null = $process.Handle
    }
    finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
    try {
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
        $process.ProcessorAffinity = $LoadAffinity
    }
    catch {
        if ($process.HasExited) { return $process }
        # Nothing tracks the process until it is returned, so a failure here must not leave it running.
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw
    }
    return $process
}

# Waits for a process while the watchdog runs. Returns $null when it exits on its own, or why it was stopped.
function Wait-UnderWatch([System.Diagnostics.Process] $Process, [int] $TimeoutMinutes) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not $Process.WaitForExit($WatchIntervalMs)) {
        $script:AbortReason = Get-RunAbortReason
        $reason = if ($script:AbortReason) { $script:AbortReason }
            elseif ((Get-Date) -ge $deadline) { "it hit its $TimeoutMinutes-minute limit" }
        if ($reason) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            return $reason
        }
    }
    return $null
}

function Build-Target {
    Write-Host 'Building the load target (Release)...'
    # No build servers, so no MSBuild or compiler process lingers after the build.
    $arguments = @('build', "`"$(Join-Path $TargetProject 'Presentation.ExecutionApi.LoadTarget.csproj')`"", '-c', 'Release',
        '--nologo', '-v', 'quiet', '-m:2', '--disable-build-servers')
    $build = Start-LoadProcess dotnet $arguments
    $stopped = Wait-UnderWatch $build $BuildTimeoutMinutes
    if ($stopped) { throw "The build was stopped: $stopped." }
    if ($build.ExitCode -ne 0) { throw "Building the load target failed (exit $($build.ExitCode))." }
}

function Start-Target([string] $OutDir) {
    $fixtures = Join-Path $OutDir 'fixtures'
    $arguments = @("`"$TargetDll`"", '--out', "`"$fixtures`"", '--port', $Port, '--model', $Model, '--callers', $Callers)
    if ($null -ne $FailureRate) { $arguments += @('--failure-rate', $FailureRate.ToString([cultureinfo]::InvariantCulture)) }
    if ($EnableToolInvocation) { $arguments += '--enable-tool-invocation' }
    if ($WithTelemetry) { $arguments += '--with-telemetry' }

    $log = Join-Path $OutDir 'target.log'
    $redirect = @{ RedirectStandardOutput = $log; RedirectStandardError = (Join-Path $OutDir 'target.err.log') }
    # The runtime sizes its thread pool from the processor count it sees at start, so tell it the load cores.
    $process = Start-LoadProcess dotnet $arguments $redirect @{ DOTNET_PROCESSOR_COUNT = "$LoadCores" }
    Wait-ForReady $process $log
    return [pscustomobject]@{ Process = $process; Fixtures = $fixtures }
}

function Wait-ForReady([System.Diagnostics.Process] $Process, [string] $Log) {
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $deadline) {
        if ($Process.HasExited) { throw "The load target exited during start-up (exit $($Process.ExitCode)); see $Log." }
        if ((Test-Path $Log) -and (Select-String -Path $Log -Pattern $ReadySignal -SimpleMatch -Quiet)) { return }
        $script:AbortReason = Get-RunAbortReason
        if ($script:AbortReason) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            throw "start-up was stopped: $($script:AbortReason)."
        }
        Start-Sleep -Milliseconds 500
    }
    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    throw "The load target did not report ready within 2 minutes; see $Log."
}

function Start-Counters([int] $ProcessId, [string] $OutDir) {
    if (-not (Get-Command dotnet-counters -ErrorAction SilentlyContinue)) { return $null }
    $arguments = @('collect', '--process-id', $ProcessId, '--refresh-interval', '1', '--format', 'csv',
        '--output', "`"$(Join-Path $OutDir 'counters.csv')`"", '--counters', $Counters)
    $redirect = @{ RedirectStandardOutput = (Join-Path $OutDir 'counters.log'); RedirectStandardError = (Join-Path $OutDir 'counters.err.log') }
    return Start-LoadProcess dotnet-counters $arguments $redirect
}

function Invoke-K6([string] $Name, [string] $Fixtures, [string] $OutDir) {
    # Scenario inputs go to k6 as -e values, so nothing is left behind in the caller's shell.
    $arguments = @('run', '--quiet', '-e', "FIXTURES=`"$Fixtures`"", '-e', "PROFILE=$LoadProfile",
        '--summary-export', "`"$(Join-Path $OutDir 'summary.json')`"", "`"$(Join-Path $ScenarioDir "$Name.js")`"")

    $redirect = @{ RedirectStandardOutput = (Join-Path $OutDir 'k6.log'); RedirectStandardError = (Join-Path $OutDir 'k6.err.log') }
    # The dashboard export is configured only through the environment. GOMAXPROCS keeps k6's scheduler to the
    # load cores it is pinned to.
    $environment = @{ K6_WEB_DASHBOARD = 'true'; K6_WEB_DASHBOARD_EXPORT = (Join-Path $OutDir 'report.html'); GOMAXPROCS = "$LoadCores" }
    $k6 = Start-LoadProcess $K6Path $arguments $redirect $environment
    $script:K6Process = $k6

    $stopped = Wait-UnderWatch $k6 $ScenarioTimeoutMinutes[$LoadProfile]
    if ($stopped) {
        Write-Warning "$Name was stopped: $stopped."
        return "stopped: $stopped"
    }
    # k6 exits 0 when every threshold passed and 99 when one failed; anything else is a script error.
    switch ($k6.ExitCode) { 0 { 'passed' } 99 { 'thresholds failed' } default { "k6 error (exit $($k6.ExitCode))" } }
}

function Stop-Target($Target, $CountersProcess) {
    # k6 too: after Ctrl+C or an error it would otherwise keep firing at a stopped target until its own end.
    if ($script:K6Process -and -not $script:K6Process.HasExited) { Stop-Process -Id $script:K6Process.Id -Force -ErrorAction SilentlyContinue }
    $script:K6Process = $null
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
Assert-ComfyUiStopped
Assert-FreeMemory
Write-Host ("Load processes: below-normal priority, {0} of {1} cores. Whole run stops after {2} minutes." -f `
    $LoadCores, [Environment]::ProcessorCount, $RunTimeLimitMinutes[$LoadProfile])
Build-Target

$runDir = Join-Path $RepoRoot ("artifacts\load\{0:yyyyMMdd-HHmmss}-$LoadProfile" -f (Get-Date))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$results = foreach ($name in $Scenario) {
    if (-not $script:AbortReason) { $script:AbortReason = Get-RunAbortReason }
    if ($script:AbortReason) {
        [pscustomobject]@{ Scenario = $name; Result = "not run: $($script:AbortReason)" }
        continue
    }
    Invoke-Scenario $name $runDir
}

Write-Host "`nResults: $runDir"
if ($script:AbortReason) { Write-Warning "The run was stopped early: $($script:AbortReason)." }
$results | Format-Table -AutoSize | Out-String | Write-Host
if ($results | Where-Object Result -ne 'passed') { exit 1 }
