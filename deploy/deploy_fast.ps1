<#
.SYNOPSIS
  Быстрый deploy — обновляет только signaling binary на VPS (10-20 сек).

.DESCRIPTION
  Для ежедневных updates. Сохраняет:
   - signaling-data volume (telemetry.db, client-version.json, admin-audit.log)
   - .env (env vars)
   - coturn config
   - docker-compose.yml

  Что делает:
   1) Cross-compile signaling binary на Windows (GOOS=linux, CGO=0)
   2) scp binary на /opt/zconect/deploy/bin/signaling.new
   3) ssh: atomic swap + docker compose restart signaling
   4) Healthcheck

  Требует что deploy_setup.ps1 уже был запущен один раз (Docker + директории).
  Если binary сильно отличается (Dockerfile changes, go version) —
  используйте deploy_from_windows.ps1 для full rebuild.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP

  # С SSH key:
  .\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -SshKeyPath C:\keys\vps.pem

  # С admin password update (перезаписывает .env):
  .\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -UpdateAdminPasswordHash '$2a$12$...'
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$VpsIp,
  [Parameter()][string]$SshUser = "root",
  [Parameter()][string]$SshKeyPath = "",
  [Parameter()][string]$ProjectPath = "",
  [Parameter()][string]$RemoteDir = "/opt/zconect",
  [Parameter()][int]$SignalingPort = 8080,
  [Parameter()][string]$UpdateAdminPasswordHash = "",
  [Parameter()][switch]$SkipHealthcheck,
  [Parameter()][switch]$RestartOnly,
  # ─── HTTPS mode ─────────────────────────────────────────────────────────
  # Если -Domain задан → healthcheck стучится в https://$Domain/healthz,
  # endpoint URL'ы печатаются с https://. Caddy config уже должен быть на
  # сервере (загружен через deploy_setup.ps1). Если нужно обновить Caddyfile —
  # запусти deploy_setup.ps1 ещё раз (.env не тронется если не указать -ForceResetEnv).
  [Parameter()][string]$Domain = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$startTime = Get-Date

# Paths
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
  $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
  $ProjectPath = (Resolve-Path (Join-Path $scriptDir "..")).Path
}
$ServerDir = Join-Path $ProjectPath "server"
$DeployDir = Join-Path $ProjectPath "deploy"
$BinDir    = Join-Path $DeployDir "bin"
$BinaryPath = Join-Path $BinDir "signaling"

function Require-Command([string]$Name) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "Command not found: $Name"
  }
}
Require-Command "ssh"
Require-Command "scp"
if (-not $RestartOnly) { Require-Command "go" }

$remoteHost = "$SshUser@$VpsIp"
$sshArgs = @("-o", "StrictHostKeyChecking=accept-new")
if ($SshKeyPath -and (Test-Path $SshKeyPath)) { $sshArgs += @("-i", $SshKeyPath) }

# 1. Cross-compile (unless RestartOnly)
if (-not $RestartOnly) {
  Write-Host "[1/4] Cross-compile signaling (linux/amd64)..." -ForegroundColor Cyan
  New-Item -ItemType Directory -Force -Path $BinDir | Out-Null

  $env:GOOS = "linux"
  $env:GOARCH = "amd64"
  $env:CGO_ENABLED = "0"

  Push-Location $ServerDir
  try {
    & go build -trimpath -ldflags="-s -w" -o $BinaryPath ./cmd/signaling
    if ($LASTEXITCODE -ne 0) { throw "go build failed" }
  }
  finally {
    Pop-Location
    $env:GOOS = ""; $env:GOARCH = ""; $env:CGO_ENABLED = ""
  }

  $binSize = [math]::Round((Get-Item $BinaryPath).Length / 1MB, 1)
  Write-Host "  Binary size: $binSize MB" -ForegroundColor DarkGray
}
else {
  if (-not (Test-Path $BinaryPath)) {
    throw "Binary not found ($BinaryPath). Run without -RestartOnly first."
  }
  Write-Host "[1/4] -RestartOnly: using existing $(Split-Path -Leaf $BinaryPath)" -ForegroundColor DarkGray
}

# 2. Upload binary to .new (atomic swap done on remote)
Write-Host "[2/4] Uploading binary to VPS..." -ForegroundColor Cyan

if (-not $RestartOnly) {
  & scp @sshArgs $BinaryPath "$remoteHost`:$RemoteDir/deploy/bin/signaling.new"
  if ($LASTEXITCODE -ne 0) { throw "scp binary failed" }

  # Sync static landing page (index.html explicitly) — avoid glob чтобы случайно
  # не leak dev-файлы (.env.local, notes.md и т.д.) если кто-то положит их в public/.
  $localIndexHtml = Join-Path $DeployDir "public\index.html"
  if (Test-Path $localIndexHtml) {
    & ssh @sshArgs $remoteHost "mkdir -p '$RemoteDir/deploy/public'"
    & scp @sshArgs "$localIndexHtml" "$remoteHost`:$RemoteDir/deploy/public/index.html" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "scp index.html failed — landing page не обновлена" }
    else { Write-Host "  (landing page synced)" -ForegroundColor DarkGray }
  }
}

# 3. Remote: swap + optional admin hash update + restart container
Write-Host "[3/4] Atomic swap + restart container..." -ForegroundColor Cyan

$hasHashUpdate = -not [string]::IsNullOrWhiteSpace($UpdateAdminPasswordHash)
$bAdminHash = if ($hasHashUpdate) { "'" + ($UpdateAdminPasswordHash -replace "'", "'""'""'") + "'" } else { "''" }
$bRemoteDir = "'$RemoteDir'"
$bRestartOnly = if ($RestartOnly.IsPresent) { "1" } else { "0" }
$bHasHashUpdate = if ($hasHashUpdate) { "1" } else { "0" }

$restartScript = @'
set -euo pipefail
REMOTE_DIR=__REMOTE_DIR__
ADMIN_HASH=__ADMIN_HASH__
RESTART_ONLY=__RESTART_ONLY__
HAS_HASH_UPDATE=__HAS_HASH_UPDATE__

cd "$REMOTE_DIR/deploy"

if [ "$RESTART_ONLY" != "1" ]; then
  if [ ! -f bin/signaling.new ]; then
    echo "ERROR: bin/signaling.new not found - scp did not upload"
    exit 1
  fi
  chmod +x bin/signaling.new
  # docker compose stop releases mmap so atomic mv can replace the running binary
  docker compose stop signaling 2>/dev/null || true
  mv bin/signaling.new bin/signaling
  echo "binary swapped"
  RESTART_CMD="up -d signaling"
else
  # -RestartOnly: just restart to clear in-memory state (rate limiter, admin sessions)
  RESTART_CMD="restart signaling"
fi

# Optional: update ADMIN_PASSWORD_HASH in .env with $ escape for docker-compose.
if [ "$HAS_HASH_UPDATE" = "1" ]; then
  if [ -f .env ]; then
    ESC_HASH=$(echo "$ADMIN_HASH" | sed 's/\$/\$\$/g')
    grep -v '^ADMIN_PASSWORD_HASH=' .env > .env.tmp || true
    echo "ADMIN_PASSWORD_HASH=$ESC_HASH" >> .env.tmp
    mv .env.tmp .env
    echo "ADMIN_PASSWORD_HASH updated in .env (escaped \$ -> \$\$)"
  else
    echo "WARN: .env not found - ADMIN_PASSWORD_HASH not updated"
  fi
fi

echo "restarting signaling container ($RESTART_CMD)..."
docker compose $RESTART_CMD

sleep 1
docker compose ps signaling
'@

$restartScript = $restartScript.
  Replace("__REMOTE_DIR__", $bRemoteDir).
  Replace("__ADMIN_HASH__", $bAdminHash).
  Replace("__RESTART_ONLY__", $bRestartOnly).
  Replace("__HAS_HASH_UPDATE__", $bHasHashUpdate)

$restartScript = $restartScript -replace "`r`n", "`n"
$tmpScript = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tmpScript -Value $restartScript -Encoding UTF8 -NoNewline
(Get-Content -Path $tmpScript -Raw) | & ssh @sshArgs $remoteHost "tr -d '\r' | bash"
if ($LASTEXITCODE -ne 0) { throw "Remote restart failed" }
Remove-Item $tmpScript -ErrorAction SilentlyContinue

# 4. Healthcheck
$httpsMode = -not [string]::IsNullOrWhiteSpace($Domain)
if (-not $SkipHealthcheck) {
  Write-Host "[4/4] Healthcheck..." -ForegroundColor Cyan
  Start-Sleep -Seconds 2

  # HTTPS mode: stucking в https://$Domain/healthz через Caddy.
  # HTTP mode: direct на VPS IP:PORT.
  $healthUrl = if ($httpsMode) {
    "https://${Domain}/healthz"
  } else {
    "http://${VpsIp}:${SignalingPort}/healthz"
  }
  Write-Host "  URL: $healthUrl" -ForegroundColor DarkGray

  $maxAttempts = 10
  $success = $false
  for ($i = 1; $i -le $maxAttempts; $i++) {
    try {
      $resp = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 3 -UseBasicParsing -ErrorAction SilentlyContinue
      if ($resp -and $resp.StatusCode -eq 200) {
        Write-Host "  healthz OK (attempt $i)" -ForegroundColor Green
        $success = $true
        break
      }
    } catch {}
    Start-Sleep -Seconds 1
  }
  if (-not $success) {
    $logsCmd = if ($httpsMode) {
      "ssh $remoteHost 'cd $RemoteDir/deploy && docker compose logs --tail=50 signaling caddy'"
    } else {
      "ssh $remoteHost 'cd $RemoteDir/deploy && docker compose logs --tail=50 signaling'"
    }
    Write-Warning "healthz failed after $maxAttempts attempts - check: $logsCmd"
  }
}

$elapsed = [int]((Get-Date) - $startTime).TotalSeconds
Write-Host ""
Write-Host "Deploy finished in $elapsed sec." -ForegroundColor Green
if ($httpsMode) {
  Write-Host "API:    https://${Domain}"           -ForegroundColor Cyan
  Write-Host "WS:     wss://${Domain}/ws"           -ForegroundColor Cyan
  Write-Host "Admin:  https://${Domain}/admin/login" -ForegroundColor Yellow
} else {
  Write-Host "API:    http://${VpsIp}:${SignalingPort}"
  Write-Host "Admin:  http://${VpsIp}:${SignalingPort}/admin/login"
}
