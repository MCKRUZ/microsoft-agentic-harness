<#
.SYNOPSIS
    Start OpenTelemetry Collector for Agentic Harness

.DESCRIPTION
    Starts the OpenTelemetry Collector in Docker or as a standalone binary.
    Automatically loads environment variables from .env file.

.PARAMETER Mode
    Deployment mode: "docker" (default) or "binary"

.PARAMETER Background
    Run in background mode (detached). Only for docker mode.

.EXAMPLE
    .\start-collector.ps1
    Starts Collector via Docker Compose in foreground

.EXAMPLE
    .\start-collector.ps1 -Background
    Starts Collector via Docker Compose in background

.EXAMPLE
    .\start-collector.ps1 -Mode binary
    Downloads and starts Collector as Windows binary
#>

param(
    [Parameter()]
    [ValidateSet("docker", "binary")]
    [string]$Mode = "docker",

    [Parameter()]
    [switch]$Background
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Info { param($Message) Write-Host "[..] $Message" -ForegroundColor Cyan }

function Load-EnvFile {
    $envFile = Join-Path $scriptDir ".env"

    if (-not (Test-Path $envFile)) {
        Write-Host "[!!] .env file not found" -ForegroundColor Yellow
        Write-Info "Copy .env.template to .env and fill in your values:"
        Write-Host "  cp .env.template .env" -ForegroundColor Gray
        Write-Host ""

        $continue = Read-Host "Continue without .env? (y/N)"
        if ($continue -ne "y" -and $continue -ne "Y") {
            exit 1
        }
        return
    }

    Write-Info "Loading environment variables from .env"

    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]+?)\s*=\s*(.+?)\s*$') {
            $key = $matches[1]
            $value = $matches[2].Trim('"').Trim("'")
            [Environment]::SetEnvironmentVariable($key, $value, "Process")
            Write-Host "  Set $key" -ForegroundColor DarkGray
        }
    }
}

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

function Start-DockerCollector {
    param([bool]$Detached)

    Write-Info "Starting Collector via Docker Compose..."

    if (-not (Test-Docker)) {
        Write-Host "[!!] Docker is not installed or not running" -ForegroundColor Red
        Write-Host "Install Docker Desktop: https://www.docker.com/products/docker-desktop" -ForegroundColor Gray
        exit 1
    }

    Push-Location $scriptDir

    try {
        $composeArgs = @("up")
        if ($Detached) {
            $composeArgs += "-d"
        }

        docker-compose $composeArgs

        if ($LASTEXITCODE -eq 0) {
            Write-Success "Collector started successfully!"
            Write-Host ""
            Write-Host "Endpoints:" -ForegroundColor Yellow
            Write-Host "  OTLP gRPC:         http://localhost:4317" -ForegroundColor Cyan
            Write-Host "  OTLP HTTP:         http://localhost:4318" -ForegroundColor Cyan
            Write-Host "  Prometheus:        http://localhost:8889/metrics" -ForegroundColor Cyan
            Write-Host "  Jaeger UI:         http://localhost:16686" -ForegroundColor Cyan
            Write-Host "  Health Check:      http://localhost:13133/health" -ForegroundColor Cyan
            Write-Host "  Collector Metrics: http://localhost:8888/metrics" -ForegroundColor Cyan
            Write-Host "  zpages Debug:      http://localhost:55679/debug/tracez" -ForegroundColor Cyan
            Write-Host ""

            if ($Detached) {
                Write-Info "Running in background. To view logs:"
                Write-Host "  docker-compose logs -f otel-collector" -ForegroundColor Gray
            }
            else {
                Write-Info "Running in foreground. Press Ctrl+C to stop."
            }
        }
        else {
            Write-Host "[!!] Failed to start Collector" -ForegroundColor Red
            exit 1
        }
    }
    finally {
        Pop-Location
    }
}

function Start-BinaryCollector {
    Write-Info "Starting Collector as Windows binary..."

    $collectorPath = Join-Path $scriptDir "otelcol-contrib.exe"
    $version = "0.115.0"

    if (-not (Test-Path $collectorPath)) {
        Write-Info "Downloading Collector v$version..."

        $url = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v$version/otelcol-contrib_${version}_windows_amd64.tar.gz"
        $tarPath = Join-Path $scriptDir "otelcol.tar.gz"

        try {
            Invoke-WebRequest -Uri $url -OutFile $tarPath -UseBasicParsing
            Write-Success "Downloaded Collector"

            Write-Info "Extracting..."
            tar -xzf $tarPath -C $scriptDir
            Remove-Item $tarPath

            Write-Success "Extracted Collector binary"
        }
        catch {
            Write-Host "[!!] Failed to download Collector: $_" -ForegroundColor Red
            exit 1
        }
    }

    if (-not (Test-Path $collectorPath)) {
        Write-Host "[!!] Collector binary not found at $collectorPath" -ForegroundColor Red
        exit 1
    }

    $configPath = Join-Path $scriptDir "config.yaml"
    Write-Info "Starting Collector..."
    Write-Host "  Config: $configPath" -ForegroundColor DarkGray
    Write-Host "  Binary: $collectorPath" -ForegroundColor DarkGray
    Write-Host ""

    try {
        & $collectorPath --config=$configPath
    }
    catch {
        Write-Host "[!!] Collector crashed: $_" -ForegroundColor Red
        exit 1
    }
}

# Main
try {
    Write-Host ""
    Write-Host "OpenTelemetry Collector Launcher" -ForegroundColor Magenta
    Write-Host "   Agentic Harness" -ForegroundColor DarkGray
    Write-Host ""

    Load-EnvFile
    Write-Host ""

    switch ($Mode) {
        "docker" {
            Start-DockerCollector -Detached:$Background
        }
        "binary" {
            if ($Background) {
                Write-Host "[!!] Background mode not supported for binary deployment" -ForegroundColor Yellow
                Write-Info "Launching in foreground..."
            }
            Start-BinaryCollector
        }
    }
}
catch {
    Write-Host "[!!] Unexpected error: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    exit 1
}
