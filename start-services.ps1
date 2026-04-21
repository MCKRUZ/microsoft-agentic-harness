<#
.SYNOPSIS
    Start all Agentic Harness infrastructure services

.DESCRIPTION
    Starts the full observability stack in the correct order:
    1. OTel Collector + Jaeger (creates shared Docker network)
    2. Prometheus + Grafana (connects to shared network)

.PARAMETER OTelOnly
    Start only the OTel Collector + Jaeger stack

.PARAMETER DashboardsOnly
    Start only the Prometheus + Grafana stack (requires OTel stack running)

.EXAMPLE
    .\start-services.ps1
    Starts all services in background

.EXAMPLE
    .\start-services.ps1 -OTelOnly
    Starts only OTel Collector + Jaeger
#>

param(
    [Parameter()]
    [switch]$OTelOnly,

    [Parameter()]
    [switch]$DashboardsOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Info { param($Message) Write-Host "[..] $Message" -ForegroundColor Cyan }
function Write-Warn { param($Message) Write-Host "[!!] $Message" -ForegroundColor Yellow }

function Test-Docker {
    try {
        $null = docker --version 2>$null
        $null = docker ps 2>$null
        return $true
    }
    catch {
        return $false
    }
}

function Start-Stack {
    param(
        [string]$Path,
        [string]$Name
    )

    Write-Info "Starting $Name..."
    Push-Location $Path

    try {
        docker-compose up -d 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Success "$Name started"
        }
        else {
            Write-Host "[!!] Failed to start $Name" -ForegroundColor Red
            exit 1
        }
    }
    finally {
        Pop-Location
    }
}

function Wait-Healthy {
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds = 60
    )

    Write-Info "Waiting for $ContainerName to be healthy..."
    $elapsed = 0

    while ($elapsed -lt $TimeoutSeconds) {
        $status = docker inspect --format '{{.State.Health.Status}}' $ContainerName 2>$null

        if ($status -eq "healthy") {
            Write-Success "$ContainerName is healthy"
            return
        }

        if ($status -eq "") {
            Write-Warn "$ContainerName has no health check, skipping wait"
            return
        }

        Start-Sleep -Seconds 3
        $elapsed += 3
    }

    Write-Warn "$ContainerName did not become healthy within ${TimeoutSeconds}s (current: $status)"
}

# Main
try {
    Write-Host ""
    Write-Host "Agentic Harness Services" -ForegroundColor Magenta
    Write-Host ""

    if (-not (Test-Docker)) {
        Write-Host "[!!] Docker is not installed or not running" -ForegroundColor Red
        exit 1
    }

    $otelDir = Join-Path $repoRoot "scripts\otel-collector"
    $dashDir = Join-Path $repoRoot "Dashboards"

    # Stack 1: OTel Collector + Jaeger
    if (-not $DashboardsOnly) {
        Start-Stack -Path $otelDir -Name "OTel Collector + Jaeger"
        Wait-Healthy -ContainerName "agentic-harness-jaeger"
        Write-Host ""
    }

    # Stack 2: Prometheus + Grafana
    if (-not $OTelOnly) {
        $network = docker network ls --filter "name=agentic-harness-otel" --format "{{.Name}}" 2>$null
        if (-not $network) {
            Write-Host "[!!] Network 'agentic-harness-otel' not found. Start OTel stack first." -ForegroundColor Red
            Write-Host "  Run: .\start-services.ps1" -ForegroundColor Gray
            exit 1
        }

        Start-Stack -Path $dashDir -Name "Prometheus + Grafana"
        Wait-Healthy -ContainerName "agentic-harness-prometheus"
        Wait-Healthy -ContainerName "agentic-harness-grafana"
        Write-Host ""
    }

    Write-Success "All services started!"
    Write-Host ""
    Write-Host "Endpoints:" -ForegroundColor Yellow
    if (-not $DashboardsOnly) {
        Write-Host "  Jaeger UI:         http://localhost:16686" -ForegroundColor Cyan
        Write-Host "  OTLP gRPC:         http://localhost:4317" -ForegroundColor Cyan
        Write-Host "  OTLP HTTP:         http://localhost:4318" -ForegroundColor Cyan
        Write-Host "  Collector Health:  http://localhost:13133/health" -ForegroundColor Cyan
    }
    if (-not $OTelOnly) {
        Write-Host "  Grafana:           http://localhost:3000  (admin/admin)" -ForegroundColor Cyan
        Write-Host "  Prometheus:        http://localhost:9090" -ForegroundColor Cyan
    }
    Write-Host ""
}
catch {
    Write-Host "[!!] Unexpected error: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    exit 1
}
