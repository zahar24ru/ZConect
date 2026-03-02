<#
.SYNOPSIS
  Собирает релизный бандл в указанную папку.

.DESCRIPTION
  - dotnet publish клиента (Release, win-x64)
  - dotnet build клиента (Debug, win-x64)
  - копирует server/deploy/docs + README.md
  - пишет VERSION.txt и START_HERE.md
  - опционально делает zip архив

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\build_release_bundle.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\build_release_bundle.ps1 -OutputDir C:\Temp\Zconnect-v1 -Zip:$false
#>

[CmdletBinding()]
param(
  [Parameter()]
  [string]$BundleName = "Zconnect-v1",

  [Parameter()]
  [string]$OutputDir = "",

  [Parameter()]
  [bool]$Zip = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Command([string]$Name) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "Command not found: $Name"
  }
}

function Write-Utf8BomFile([string]$Path, [string]$Content) {
  $dir = Split-Path -Parent $Path
  if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
  }
  # Always write UTF-8 with BOM so editors auto-detect Cyrillic correctly.
  $enc = New-Object System.Text.UTF8Encoding($true)
  [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

function Robocopy-Dir([string]$From, [string]$To) {
  if (-not (Test-Path $From -PathType Container)) {
    throw "Source directory not found: $From"
  }
  New-Item -ItemType Directory -Path $To -Force | Out-Null

  # robocopy exit codes: 0..7 = success (incl. copied/skipped), 8+ = failure
  & robocopy $From $To /E /R:2 /W:1 `
    /XD "bin" "obj" ".vs" ".idea" ".git" ".cursor" "Zconnect-v1" "ZConect-v1.1" `
    /XF "*.user" "*.suo" "*.tmp" `
    /NFL /NDL /NJH /NJS /NP | Out-Null

  if ($LASTEXITCODE -ge 8) {
    throw "robocopy failed (exit code $LASTEXITCODE) from '$From' to '$To'"
  }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
  $OutputDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $BundleName))
} else {
  $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
}

Require-Command "dotnet"
Require-Command "robocopy"

Write-Host "[1/4] Preparing output dir: $OutputDir" -ForegroundColor Cyan
if (Test-Path $OutputDir) {
  # Safety: only auto-clean known bundle-like names.
  $leaf = (Split-Path -Leaf $OutputDir)
  if ($leaf -notmatch '^ZCon(?:nect|ect)-v\d+(\.\d+)?$') {
    throw "Refusing to delete existing OutputDir (unexpected leaf): $OutputDir"
  }
  Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

Write-Host "[2/4] Building Windows client (Release+Debug, win-x64)..." -ForegroundColor Cyan
$clientReleaseOut = Join-Path $OutputDir "client\release\win-x64"
$clientDebugOut = Join-Path $OutputDir "client\debug\win-x64"
New-Item -ItemType Directory -Path $clientReleaseOut -Force | Out-Null
New-Item -ItemType Directory -Path $clientDebugOut -Force | Out-Null

& dotnet publish (Join-Path $repoRoot "client\UiApp\UiApp.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $clientReleaseOut

if (-not (Test-Path (Join-Path $clientReleaseOut "UiApp.exe"))) {
  throw "Publish did not produce UiApp.exe in: $clientReleaseOut"
}

& dotnet build (Join-Path $repoRoot "client\UiApp\UiApp.csproj") `
  -c Debug `
  -p:RestorePackagesPath="$env:USERPROFILE\.nuget\packages"

$debugBuildOut = Join-Path $repoRoot "client\UiApp\bin\Debug\net8.0-windows\win-x64"
if (-not (Test-Path (Join-Path $debugBuildOut "UiApp.exe"))) {
  throw "Debug build did not produce UiApp.exe in: $debugBuildOut"
}
Copy-Item -Recurse -Force (Join-Path $debugBuildOut "*") $clientDebugOut

Write-Host "[3/4] Copying server/deploy/docs..." -ForegroundColor Cyan
Copy-Item -Recurse -Force (Join-Path $repoRoot "server") (Join-Path $OutputDir "server")
Copy-Item -Recurse -Force (Join-Path $repoRoot "deploy") (Join-Path $OutputDir "deploy")
Copy-Item -Recurse -Force (Join-Path $repoRoot "docs") (Join-Path $OutputDir "docs")
Copy-Item -Force (Join-Path $repoRoot "README.md") (Join-Path $OutputDir "README.md")

$srcOut = Join-Path $OutputDir "src"
Write-Host "  Snapshot sources -> $srcOut" -ForegroundColor DarkGray
Robocopy-Dir (Join-Path $repoRoot "client") (Join-Path $srcOut "client")
Robocopy-Dir (Join-Path $repoRoot "server") (Join-Path $srcOut "server")
Robocopy-Dir (Join-Path $repoRoot "deploy") (Join-Path $srcOut "deploy")
Robocopy-Dir (Join-Path $repoRoot "docs") (Join-Path $srcOut "docs")
if (Test-Path (Join-Path $repoRoot "tools") -PathType Container) {
  Robocopy-Dir (Join-Path $repoRoot "tools") (Join-Path $srcOut "tools")
}
Copy-Item -Force (Join-Path $repoRoot "README.md") (Join-Path $srcOut "README.md")

$stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$dotnetVer = (& dotnet --version) 2>$null
$versionText = @"
$BundleName build
Timestamp: $stamp
Dotnet:    $dotnetVer
"@
Write-Utf8BomFile (Join-Path $OutputDir "VERSION.txt") $versionText

$startHereText = @"
START HERE

Client Release:
  client\release\win-x64\UiApp.exe

Client Debug:
  client\debug\win-x64\UiApp.exe

Server deploy (from Windows -> Ubuntu VPS):
  docs\DEPLOY_FROM_WINDOWS.md

Notes:
  - FFmpeg is not included. Set the path to ffmpeg.exe in the client settings (VP8 Probe).
  - To control the remote screen window: click inside the window to focus it.
"@
Write-Utf8BomFile (Join-Path $OutputDir "START_HERE.md") $startHereText

if ($Zip) {
  Write-Host "[4/4] Creating zip..." -ForegroundColor Cyan
  $zipPath = $OutputDir.TrimEnd('\') + ".zip"
  if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
  Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -Force
  Write-Host "  Zip: $zipPath" -ForegroundColor DarkGray
} else {
  Write-Host "[4/4] Zip disabled." -ForegroundColor DarkGray
}

Write-Host "OK" -ForegroundColor Green

