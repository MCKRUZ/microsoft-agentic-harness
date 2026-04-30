<#
.SYNOPSIS
    Start the full observability infrastructure for the Agentic Harness.

.DESCRIPTION
    Launches the complete observability stack in dependency order:
      1. OTel Collector + Tempo  (creates the shared Docker network)
      2. PostgreSQL + Prometheus + Grafana  (joins the shared network)

    All containers run in the background. Use stop-infrastructure.ps1 to tear down.

.PARAMETER Down
    Tear down all infrastructure containers instead of starting them.

.EXAMPLE
    .\start-infrastructure.ps1
    Start all observability containers in background.

.EXAMPLE
    .\start-infrastructure.ps1 -Down
    Stop and remove all observability containers.
#>

param(
    [switch]$Down
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$otelDir = Join-Path $repoRoot "scripts\otel-collector"
$dashDir = Join-Path $repoRoot "Dashboards"

function Write-Status($Icon, $Message, $Color) {
    Write-Host "[$Icon] $Message" -ForegroundColor $Color
}

function Test-Docker {
    try {
        $null = docker --version 2>$null
        $null = docker ps 2>$null
        return $true
    }
    catch { return $false }
}

# ── Main ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Agentic Harness — Infrastructure" -ForegroundColor Magenta
Write-Host ""

if (-not (Test-Docker)) {
    Write-Status "!!" "Docker is not installed or not running." Red
    Write-Host "   Install Docker Desktop: https://www.docker.com/products/docker-desktop" -ForegroundColor Gray
    exit 1
}

if ($Down) {
    Write-Status ".." "Stopping Dashboards stack (PostgreSQL, Prometheus, Grafana)..." Cyan
    docker compose -f "$dashDir\docker-compose.yml" down 2>$null

    Write-Status ".." "Stopping OTel stack (Collector, Tempo)..." Cyan
    docker compose -f "$otelDir\docker-compose.yml" down 2>$null

    Write-Status "OK" "All infrastructure stopped." Green
    exit 0
}

# 1. OTel Collector + Tempo (creates the agentic-harness-otel network)
Write-Status ".." "Starting OTel Collector + Tempo..." Cyan
docker compose -f "$otelDir\docker-compose.yml" up -d

if ($LASTEXITCODE -ne 0) {
    Write-Status "!!" "Failed to start OTel stack." Red
    exit 1
}
Write-Status "OK" "OTel Collector + Tempo running." Green

# 2. Dashboards stack (PostgreSQL, Prometheus, Grafana)
Write-Status ".." "Starting PostgreSQL + Prometheus + Grafana..." Cyan
docker compose -f "$dashDir\docker-compose.yml" up -d

if ($LASTEXITCODE -ne 0) {
    Write-Status "!!" "Failed to start Dashboards stack." Red
    exit 1
}
Write-Status "OK" "PostgreSQL + Prometheus + Grafana running." Green

# 3. Wait for health checks
Write-Host ""
Write-Status ".." "Waiting for services to become healthy..." Cyan

$services = @(
    @{ Name = "PostgreSQL";     Cmd = "docker inspect --format='{{.State.Health.Status}}' agentic-harness-postgres" }
    @{ Name = "Prometheus";     Cmd = "docker inspect --format='{{.State.Health.Status}}' agentic-harness-prometheus" }
    @{ Name = "OTel Collector"; Cmd = "docker inspect --format='{{.State.Health.Status}}' agentic-harness-otel-collector" }
)

$maxWait = 60
$elapsed = 0
$allHealthy = $false

while ($elapsed -lt $maxWait) {
    $allHealthy = $true
    foreach ($svc in $services) {
        $status = (Invoke-Expression $svc.Cmd 2>$null).Trim("'")
        if ($status -ne "healthy") {
            $allHealthy = $false
            break
        }
    }
    if ($allHealthy) { break }
    Start-Sleep -Seconds 2
    $elapsed += 2
}

if (-not $allHealthy) {
    Write-Status "!!" "Some services did not become healthy within ${maxWait}s. Check 'docker ps' for status." Yellow
}

# 4. Summary
Write-Host ""
Write-Host "Infrastructure Endpoints:" -ForegroundColor Yellow
Write-Host "  PostgreSQL:        localhost:5432  (observability/observability)" -ForegroundColor Cyan
Write-Host "  Prometheus:        http://localhost:9090" -ForegroundColor Cyan
Write-Host "  Grafana:           http://localhost:3000  (admin/admin)" -ForegroundColor Cyan
Write-Host "  OTLP gRPC:        http://localhost:4317" -ForegroundColor Cyan
Write-Host "  OTLP HTTP:        http://localhost:4318" -ForegroundColor Cyan
Write-Host "  Tempo (traces):   http://localhost:3200" -ForegroundColor Cyan
Write-Host ""
Write-Status "OK" "Ready. Start AgentHub with: dotnet run --project src/Content/Presentation/Presentation.AgentHub" Green
Write-Host ""
