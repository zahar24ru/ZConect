# ZConect installer build script.
# Publishes UI + Service (framework-dependent win-x64) into installer/staging/,
# then invokes Inno Setup Compiler (ISCC.exe) to produce the installer exe.
#
# Usage (from repo root):
#     powershell -ExecutionPolicy Bypass -File installer/build.ps1
#     powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#     powershell -ExecutionPolicy Bypass -File installer/build.ps1 -SkipPublish

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Iscc = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerDir = $PSScriptRoot
$stagingDir = Join-Path $installerDir 'staging'
$outputDir = Join-Path $installerDir 'output'

Write-Host "Repo root:     $repoRoot"
Write-Host "Installer dir: $installerDir"
Write-Host "Staging dir:   $stagingDir"

# 1. Publish projects
if (-not $SkipPublish) {
    if (Test-Path $stagingDir) {
        Write-Host "Cleaning staging directory..."
        Remove-Item -Recurse -Force $stagingDir
    }
    New-Item -ItemType Directory -Path $stagingDir | Out-Null

    Write-Host "Publishing UiApp -> staging..."
    & dotnet publish (Join-Path $repoRoot 'client/UiApp/UiApp.csproj') `
        -c $Configuration `
        -r win-x64 `
        --self-contained false `
        -o $stagingDir `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish UiApp failed ($LASTEXITCODE)" }

    Write-Host "Publishing ZConectService -> staging..."
    & dotnet publish (Join-Path $repoRoot 'client/ZConectService/ZConectService.csproj') `
        -c $Configuration `
        -r win-x64 `
        --self-contained false `
        -o $stagingDir `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ZConectService failed ($LASTEXITCODE)" }

    Write-Host "Publishing ZConectInputHelper -> staging..."
    & dotnet publish (Join-Path $repoRoot 'client/ZConectInputHelper/ZConectInputHelper.csproj') `
        -c $Configuration `
        -r win-x64 `
        --self-contained false `
        -o $stagingDir `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ZConectInputHelper failed ($LASTEXITCODE)" }

    foreach ($req in 'ZConnect.exe', 'ZConectService.exe', 'ZConectInputHelper.exe') {
        $p = Join-Path $stagingDir $req
        if (-not (Test-Path $p)) { throw "Missing expected binary: $p" }
    }
    Write-Host "Publish complete."
} else {
    Write-Host "-SkipPublish specified, reusing existing staging/"
    if (-not (Test-Path (Join-Path $stagingDir 'ZConnect.exe'))) {
        throw "staging/ZConnect.exe missing - cannot skip publish on empty staging"
    }
}

# 2. Locate ISCC.exe
if (-not $Iscc) {
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 5\ISCC.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $Iscc = $c; break }
    }
}

if (-not $Iscc -or -not (Test-Path $Iscc)) {
    Write-Error "ISCC.exe not found. Install Inno Setup from https://jrsoftware.org/isdl.php or pass -Iscc <path>."
    exit 1
}
Write-Host "Using ISCC: $Iscc"

# 3. Run ISCC
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir | Out-Null }

$iss = Join-Path $installerDir 'ZConect.iss'
Write-Host "Compiling installer: $iss"
& $Iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

# 4. Report
$produced = Get-ChildItem $outputDir -Filter 'ZConnect-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($produced) {
    $sizeMb = [math]::Round($produced.Length / 1MB, 2)
    $sha = (Get-FileHash -Algorithm SHA256 $produced.FullName).Hash
    Write-Host ""
    Write-Host "SUCCESS: Installer built at $($produced.FullName) ($sizeMb MB)"
    Write-Host "SHA256:  $sha"
} else {
    Write-Warning "ISCC reported success but no ZConnect-Setup-*.exe found in $outputDir"
}
