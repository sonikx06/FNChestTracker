<#
.SYNOPSIS
  Builds the self-contained Chest Tracker release: desktop client (win-x64) and dedicated server
  (win-x64, linux-x64), copies configuration, launch scripts and documentation, checks that no secrets
  are included, and produces a ZIP archive.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-release.ps1
  powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -SkipTests -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputName = 'ChestTracker-Release',
    [switch]$SkipTests,
    [switch]$SkipZip,
    # Release assets are signed for the in-app updater with the key from tools/ReleaseSigner (see CONTRIBUTING.md).
    [string]$SigningKey = (Join-Path $env:USERPROFILE '.fnchesttracker\update-signing-key.pem'),
    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root $OutputName
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

function Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Invoke-Dotnet([string[]]$arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Copy-Lf([string]$source, [string]$destination) {
    # Shell scripts and unit files must have Unix line endings.
    $text = [IO.File]::ReadAllText($source) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($destination, $text, (New-Object Text.UTF8Encoding($false)))
}

Write-Host "Chest Tracker release $version ($Configuration)" -ForegroundColor Green
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK 8 is required to build (not to run) the release.' }

# ------------------------------------------------------------------ 0. tests
if (-not $SkipTests) {
    Step 'Running unit tests'
    Invoke-Dotnet @('test', (Join-Path $root 'tests/ChestTracker.Tests'), '-c', $Configuration, '--nologo')
    # Server tests need PostgreSQL (CHESTTRACKER_TEST_PG or tools/pg-dev-connection.txt); they skip themselves otherwise.
    Invoke-Dotnet @('test', (Join-Path $root 'tests/ChestTracker.Server.Tests'), '-c', $Configuration, '--nologo')
}

# ------------------------------------------------------------------ 1. clean
Step "Preparing $out"
if (Test-Path $out) {
    $resolved = (Resolve-Path $out).Path
    if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $root) {
        throw "Refusing to delete $resolved (outside the repository)."
    }
    Remove-Item $resolved -Recurse -Force
}
$clientDir = Join-Path $out 'Client'
$serverDir = Join-Path $out 'Server'
$winDir = Join-Path $serverDir 'Windows'
$linuxDir = Join-Path $serverDir 'Linux'
$docsDir = Join-Path $out 'Documentation'
New-Item -ItemType Directory -Force $clientDir, $winDir, $linuxDir, $docsDir | Out-Null

$common = @('-c', $Configuration, '--self-contained', 'true', '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo')

# ------------------------------------------------------------------ 2. client
Step 'Publishing desktop client (win-x64, self-contained single file)'
# One executable (runtime, WPF, ONNX Runtime, DirectML and OpenCV inside; native parts are unpacked to %TEMP%\.net on
# first start) plus models\best.onnx, which is opened from disk. No trimming: WPF and the reflection-based libraries
# are not trim-safe.
Invoke-Dotnet (@('publish', (Join-Path $root 'src/ChestTracker.App'), '-r', 'win-x64', '-o', $clientDir,
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') + $common)
$unexpected = Get-ChildItem $clientDir -Recurse -File | Where-Object { $_.Name -notin @('FortniteChestTracker.exe', 'best.onnx') }
if ($unexpected) { throw "Unexpected files in the single-file client: $($unexpected.Name -join ', ')" }
$model = Join-Path $clientDir 'models/best.onnx'
if (-not (Test-Path $model)) { throw "The chest model is missing from the client: $model (expected src/ChestTracker.App/models/best.onnx)." }

# ------------------------------------------------------------------ 3. servers
foreach ($target in @(@{ Rid = 'win-x64'; Dir = $winDir }, @{ Rid = 'linux-x64'; Dir = $linuxDir })) {
    Step "Publishing dedicated server ($($target.Rid), self-contained single file)"
    Invoke-Dotnet (@('publish', (Join-Path $root 'src/ChestTracker.Server'), '-r', $target.Rid, '-o', $target.Dir,
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') + $common)

    # Release configuration replaces the development appsettings; no development files ship.
    Copy-Item (Join-Path $root 'deploy/server/appsettings.json') $target.Dir -Force
    Get-ChildItem $target.Dir -Filter 'appsettings.*.json' | Remove-Item -Force
    Get-ChildItem $target.Dir -Filter '*.pdb' | Remove-Item -Force
}

Copy-Item (Join-Path $root 'deploy/server/start-server.ps1') $winDir
Copy-Item (Join-Path $root 'deploy/server/start-server.cmd') $winDir
Copy-Lf (Join-Path $root 'deploy/server/start-server.sh') (Join-Path $linuxDir 'start-server.sh')
Copy-Lf (Join-Path $root 'deploy/server/chesttracker-server.service') (Join-Path $linuxDir 'chesttracker-server.service')
Copy-Lf (Join-Path $root 'deploy/server/appsettings.json') (Join-Path $linuxDir 'appsettings.json')
New-Item -ItemType Directory -Force (Join-Path $serverDir 'reverse-proxy') | Out-Null
Copy-Lf (Join-Path $root 'deploy/server/reverse-proxy/nginx-chesttracker.conf') (Join-Path $serverDir 'reverse-proxy/nginx-chesttracker.conf')
Copy-Lf (Join-Path $root 'deploy/server/reverse-proxy/Caddyfile') (Join-Path $serverDir 'reverse-proxy/Caddyfile')
Copy-Lf (Join-Path $root 'deploy/server/docker-compose.postgres.yml') (Join-Path $serverDir 'docker-compose.postgres.yml')

# ------------------------------------------------------------------ 4. docs
Step 'Copying documentation'
foreach ($dir in @($clientDir, $winDir, $linuxDir, $docsDir)) {
    Copy-Item (Join-Path $root 'LICENSE') $dir
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dir
}
Copy-Item (Join-Path $root 'CONTRIBUTING.md') $docsDir
Copy-Item (Join-Path $root 'README.md') $docsDir
Copy-Item (Join-Path $root 'deploy/docs/*.md') $docsDir
New-Item -ItemType Directory -Force (Join-Path $docsDir 'discord-assets') | Out-Null
Copy-Item (Join-Path $root 'assets/discord/*.png') (Join-Path $docsDir 'discord-assets')
@"
Chest Tracker $version
Built: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC

Client/            Windows desktop client (win-x64). Run FortniteChestTracker.exe.
Server/Windows/    Dedicated server for Windows x64. Run start-server.cmd.
Server/Linux/      Dedicated server for Linux x64. Run: bash start-server.sh
Documentation/     README.md, SERVER-SETUP.md, CLIENT-GUIDE.md

All builds are self-contained: no .NET runtime is required.
The server needs PostgreSQL 13+ (see Documentation/SERVER-SETUP.md).
"@ | Set-Content (Join-Path $out 'README.txt') -Encoding utf8

# ------------------------------------------------------------------ 5. secret check
Step 'Checking that no secrets are included'
$forbidden = Get-ChildItem $out -Recurse -File | Where-Object {
    $_.Name -in @('signing.key', 'pg-dev-connection.txt', 'settings.json', 'chests.db') -or
    $_.Extension -in @('.pfx', '.pem', '.key', '.ctbak', '.db') -or
    $_.FullName -match '[\\/](data|backups|logs)[\\/]'
}
if ($forbidden) { throw "Secret or state files found in the release:`n$($forbidden.FullName -join "`n")" }
foreach ($cfg in Get-ChildItem $serverDir -Recurse -Filter 'appsettings.json') {
    $json = Get-Content $cfg.FullName -Raw | ConvertFrom-Json
    if ($json.ChestTracker.SigningKey -or $json.ChestTracker.AdminApiKey) { throw "$($cfg.FullName) contains a key." }
    if ($json.ConnectionStrings.Postgres -notmatch 'Password=CHANGE_ME') { throw "$($cfg.FullName) contains a real database password." }
}

# ------------------------------------------------------------------ 6. checksums + zip
Step 'Writing checksums'
$sums = Get-ChildItem $out -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($out.Length + 1).Replace('\', '/')
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $rel"
}
$sums | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

if (-not $SkipZip) {
    Step 'Creating ZIP archive'
    $zip = Join-Path $root "ChestTracker-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("ZIP: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green

    # GitHub release assets: one download per audience, each extracting to a single named folder.
    Step 'Creating release assets in dist/'
    $dist = Join-Path $root 'dist'
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    New-Item -ItemType Directory -Force $dist | Out-Null
    $staging = Join-Path $dist '_staging'
    $assets = @(
        @{ Name = 'FortniteChestTracker-win-x64.zip'; Source = $clientDir; Folder = 'FortniteChestTracker' },
        @{ Name = 'FortniteChestTracker-Server-win-x64.zip'; Source = $winDir; Folder = 'FortniteChestTracker-Server' },
        @{ Name = 'FortniteChestTracker-Server-linux-x64.zip'; Source = $linuxDir; Folder = 'FortniteChestTracker-Server' }
    )
    foreach ($asset in $assets) {
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        $folder = Join-Path $staging $asset.Folder
        New-Item -ItemType Directory -Force $folder | Out-Null
        Copy-Item (Join-Path $asset.Source '*') $folder -Recurse
        if ($asset.Source -ne $clientDir) {
            Copy-Item (Join-Path $serverDir 'reverse-proxy') $folder -Recurse
            Copy-Item (Join-Path $serverDir 'docker-compose.postgres.yml') $folder
            Copy-Item (Join-Path $root 'deploy/docs/SERVER-SETUP.md') $folder
        }
        Compress-Archive -Path $folder -DestinationPath (Join-Path $dist $asset.Name) -CompressionLevel Optimal
    }
    Remove-Item $staging -Recurse -Force
    Get-ChildItem $dist -Filter *.zip | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    } | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

    # Sign the checksums: the updater installs nothing whose SHA256SUMS.txt is not signed with this project's key.
    if (Test-Path $SigningKey) {
        Invoke-Dotnet @('run', '--project', (Join-Path $root 'tools/ReleaseSigner'), '-c', $Configuration, '--', 'sign',
            (Join-Path $dist 'SHA256SUMS.txt'), $SigningKey)
    } elseif ($AllowUnsigned) {
        Write-Warning "No signing key at ${SigningKey}: release assets are NOT signed; the in-app updater will not offer them."
    } else {
        throw "Signing key not found: ${SigningKey} (create one with 'dotnet run --project tools/ReleaseSigner -- keygen', or pass -AllowUnsigned)."
    }
    Get-ChildItem $dist | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green }
}

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("`nRelease ready: {0} ({1:N1} MB)" -f $out, $size) -ForegroundColor Green
