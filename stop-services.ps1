<#
.SYNOPSIS
    Stop all Agentic Harness infrastructure services

.DESCRIPTION
    Stops the observability stack in reverse order:
    1. Prometheus + Grafana
    2. OTel Collector + Jaeger

.PARAMETER RemoveVolumes
    Remove Docker volumes when stopping (clears all stored data)

.PARAMETER OTelOnly
    Stop only the OTel Collector + Jaeger stack

.PARAMETER DashboardsOnly
    Stop only the Prometheus + Grafana stack

.EXAMPLE
    .\stop-services.ps1
    Stops all services, preserves data volumes

.EXAMPLE
    .\stop-services.ps1 -RemoveVolumes
    Stops all services and deletes stored data
#>

param(
    [Parameter()]
    [switch]$RemoveVolumes,

    [Parameter()]
    [switch]$OTelOnly,

    [Parameter()]
    [switch]$DashboardsOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Info { param($Message) Write-Host "[..] $Message" -ForegroundColor Cyan }

function Stop-Stack {
    param(
        [string]$Path,
        [string]$Name,
        [bool]$Volumes
    )

    Write-Info "Stopping $Name..."
    Push-Location $Path

    try {
        $composeArgs = @("down")
        if ($Volumes) {
            $composeArgs += "-v"
        }

        docker-compose $composeArgs 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Success "$Name stopped"
        }
        else {
            Write-Host "[!!] Failed to stop $Name" -ForegroundColor Red
        }
    }
    finally {
        Pop-Location
    }
}

# Main
try {
    Write-Host ""
    Write-Host "Stopping Agentic Harness Services" -ForegroundColor Magenta

    if ($RemoveVolumes) {
        Write-Host "  (removing volumes)" -ForegroundColor Yellow
    }

    Write-Host ""

    $otelDir = Join-Path $repoRoot "scripts\otel-collector"
    $dashDir = Join-Path $repoRoot "Dashboards"

    # Stop in reverse order: Dashboards first, then OTel
    if (-not $OTelOnly) {
        Stop-Stack -Path $dashDir -Name "Prometheus + Grafana" -Volumes $RemoveVolumes
        Write-Host ""
    }

    if (-not $DashboardsOnly) {
        Stop-Stack -Path $otelDir -Name "OTel Collector + Jaeger" -Volumes $RemoveVolumes
        Write-Host ""
    }

    Write-Success "All services stopped"
    Write-Host ""
}
catch {
    Write-Host "[!!] Unexpected error: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    exit 1
}
