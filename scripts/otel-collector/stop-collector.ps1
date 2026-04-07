<#
.SYNOPSIS
    Stop OpenTelemetry Collector for Agentic Harness

.DESCRIPTION
    Stops the running OpenTelemetry Collector containers or processes.

.PARAMETER RemoveVolumes
    Remove Docker volumes when stopping (clears all data)

.EXAMPLE
    .\stop-collector.ps1

.EXAMPLE
    .\stop-collector.ps1 -RemoveVolumes
#>

param(
    [Parameter()]
    [switch]$RemoveVolumes
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

Write-Host ""
Write-Host "Stopping OpenTelemetry Collector" -ForegroundColor Magenta
Write-Host ""

Push-Location $scriptDir

try {
    $composeArgs = @("down")
    if ($RemoveVolumes) {
        $composeArgs += "-v"
        Write-Host "[..] Removing volumes..." -ForegroundColor Cyan
    }

    docker-compose $composeArgs

    if ($LASTEXITCODE -eq 0) {
        Write-Host "[OK] Collector stopped successfully!" -ForegroundColor Green

        if ($RemoveVolumes) {
            Write-Host "[..] Volumes removed" -ForegroundColor Cyan
        }
    }
    else {
        Write-Host "[!!] Failed to stop Collector" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host "[!!] Error stopping Collector: $_" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}

Write-Host ""
