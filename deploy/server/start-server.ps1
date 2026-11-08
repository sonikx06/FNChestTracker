# Starts the Chest Tracker dedicated server (Windows). Run from this folder:
#   powershell -ExecutionPolicy Bypass -File .\start-server.ps1
# Configuration: appsettings.json next to this script, or environment variables such as
#   ConnectionStrings__Postgres, ChestTracker__AdminApiKey (they override the file).
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$exe = Join-Path $PSScriptRoot 'ChestTracker.Server.exe'
if (-not (Test-Path $exe)) { throw "ChestTracker.Server.exe not found next to this script." }

$config = Get-Content (Join-Path $PSScriptRoot 'appsettings.json') -Raw
if (-not $env:ConnectionStrings__Postgres -and $config -match 'Password=CHANGE_ME') {
    Write-Host 'appsettings.json still contains the placeholder database password (CHANGE_ME).' -ForegroundColor Yellow
    Write-Host 'Edit ConnectionStrings:Postgres (see Documentation\SERVER-SETUP.md) and run again.' -ForegroundColor Yellow
    exit 1
}

if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Production' }
Write-Host "Starting Chest Tracker server ($env:ASPNETCORE_ENVIRONMENT). Press Ctrl+C to stop."
& $exe @args
exit $LASTEXITCODE
