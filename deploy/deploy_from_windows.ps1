<#
.SYNOPSIS
  Авто-деплой ZConect на Ubuntu VPS из Windows.

.DESCRIPTION
  1) Упаковывает локальные папки `server/`, `deploy/`, `docs/` в архив
  2) Загружает архив на VPS по scp
  3) По ssh выполняет bootstrap:
     - ставит docker + docker compose plugin
     - пишет `deploy/.env`
     - (опционально) открывает порты в ufw, если ufw установлен
     - запускает `docker compose up -d --build`

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\deploy_from_windows.ps1 `
    -VpsIp 93.115.203.200 -SshUser root -TurnPass "change_me" -PublicIpv4 93.115.203.200
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$VpsIp,

  [Parameter()]
  [string]$SshUser = "root",

  [Parameter()]
  [string]$SshKeyPath = "",

  [Parameter()]
  [string]$ProjectPath = "",

  [Parameter()]
  [int]$SignalingPort = 8080,

  [Parameter()]
  [string]$TurnRealm = "zconect.local",

  [Parameter()]
  [string]$TurnUser = "zconect",

  [Parameter(Mandatory = $true)]
  [string]$TurnPass,

  [Parameter(Mandatory = $true)]
  [string]$PublicIpv4,

  [Parameter()]
  [switch]$OpenUfwPorts = $true,

  [Parameter()]
  [string]$RemoteDir = "/opt/zconect"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
  $scriptPath = $MyInvocation.MyCommand.Path
  if ([string]::IsNullOrWhiteSpace($scriptPath)) {
    throw "Cannot determine script path. Pass -ProjectPath explicitly (e.g. C:\Soft_pub\ZConect)."
  }
  $scriptDir = Split-Path -Parent $scriptPath
  $ProjectPath = (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Require-Command([string]$Name) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "Command not found: $Name. Install it and retry."
  }
}

function New-SshArgs() {
  $args = @("-o", "StrictHostKeyChecking=accept-new")
  if ($SshKeyPath -and (Test-Path $SshKeyPath)) {
    $args += @("-i", $SshKeyPath)
  }
  return ,$args
}

function Bash-SingleQuote([string]$s) {
  # Wrap in single quotes and escape embedded single quotes using: '"'"'
  return "'" + ($s -replace "'", "'""'""'") + "'"
}

Require-Command "ssh"
Require-Command "scp"
Require-Command "tar"

if (-not (Test-Path -Path $ProjectPath -PathType Container)) {
  throw "ProjectPath does not exist: $ProjectPath"
}

$remoteHost = "$SshUser@$VpsIp"
$sshArgs = New-SshArgs

Write-Host "[1/5] Packing local project..." -ForegroundColor Cyan
$tmp = [System.IO.Path]::GetTempPath()
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$archive = Join-Path $tmp ("zconect-" + $stamp + ".tar.gz")

Push-Location $ProjectPath
try {
  & tar -czf $archive `
    --exclude "./**/bin" `
    --exclude "./**/obj" `
    --exclude "./**/.vs" `
    --exclude "./**/.idea" `
    --exclude "./**/.git" `
    --exclude "./**/.cursor" `
    "server" "deploy" "docs" | Out-Null
} finally {
  Pop-Location
}

if (-not (Test-Path $archive)) {
  throw "Archive was not created: $archive"
}

Write-Host "  Archive: $archive" -ForegroundColor DarkGray

Write-Host "[2/5] Uploading archive to VPS..." -ForegroundColor Cyan
$remoteTmp = "/tmp/zconect-$stamp.tar.gz"
& scp @sshArgs $archive "$remoteHost`:$remoteTmp" | Out-Null

Write-Host "[3/5] Running remote bootstrap..." -ForegroundColor Cyan
$openUfw = if ($OpenUfwPorts.IsPresent) { "1" } else { "0" }

$bRemoteDir = Bash-SingleQuote $RemoteDir
$bRemoteTmp = Bash-SingleQuote $remoteTmp
$bSignalingPort = Bash-SingleQuote ([string]$SignalingPort)
$bTurnRealm = Bash-SingleQuote $TurnRealm
$bTurnUser = Bash-SingleQuote $TurnUser
$bTurnPass = Bash-SingleQuote $TurnPass
$bPublicIpv4 = Bash-SingleQuote $PublicIpv4
$bOpenUfw = Bash-SingleQuote $openUfw

$remoteScript = @'
set -euo pipefail

REMOTE_DIR=__REMOTE_DIR__
REMOTE_TAR=__REMOTE_TAR__
SIGNALING_PORT=__SIGNALING_PORT__
TURN_REALM=__TURN_REALM__
TURN_USER=__TURN_USER__
TURN_PASS=__TURN_PASS__
PUBLIC_IPV4=__PUBLIC_IPV4__
OPEN_UFW=__OPEN_UFW__

echo "[remote] dir=$REMOTE_DIR"
sudo mkdir -p "$REMOTE_DIR"
sudo chown -R $(id -u):$(id -g) "$REMOTE_DIR"

echo "[remote] apt update"
sudo apt-get update -y
sudo apt-get install -y ca-certificates curl git

echo "[remote] install docker + compose"
# 1) Try Ubuntu packages (sometimes compose plugin is missing on cloud images).
sudo apt-get install -y docker.io || true

# 2) Ensure 'docker compose' exists; if not, install from Docker official repo.
if ! docker compose version >/dev/null 2>&1; then
  echo "[remote] docker compose plugin missing -> installing from Docker repo"
  sudo apt-get install -y ca-certificates curl gnupg
  sudo install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  sudo chmod a+r /etc/apt/keyrings/docker.gpg
  . /etc/os-release
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -y
  # Avoid docker.io/docker-ce conflicts on some images.
  sudo apt-get remove -y docker.io docker-doc docker-compose docker-compose-v2 podman-docker containerd runc || true
  sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi

sudo systemctl enable --now docker
if ! systemctl is-active --quiet docker; then
  echo "[remote] ERROR: docker.service is not running"
  systemctl --no-pager -l status docker || true
  journalctl --no-pager -u docker.service -n 200 || true
  exit 1
fi
sudo systemctl restart docker
if ! systemctl is-active --quiet docker; then
  echo "[remote] ERROR: docker.service failed after restart"
  systemctl --no-pager -l status docker || true
  journalctl --no-pager -u docker.service -n 200 || true
  exit 1
fi
docker --version
docker compose version
sudo docker info >/dev/null

echo "[remote] unpack archive"
tar -xzf "$REMOTE_TAR" -C "$REMOTE_DIR"

echo "[remote] write deploy/.env"
cd "$REMOTE_DIR/deploy"
cat > .env <<EOF
APP_ENV=prod
SIGNALING_PORT=$SIGNALING_PORT
REDIS_ADDR=redis:6379
SESSION_TTL_SEC=300
MAX_JOIN_ATTEMPTS=5
LOCK_MINUTES=10
TURN_REALM=$TURN_REALM
TURN_USER=$TURN_USER
TURN_PASS=$TURN_PASS
PUBLIC_IPV4=$PUBLIC_IPV4
EOF

if [ "$OPEN_UFW" = "1" ]; then
  echo "[remote] ufw rules (if ufw installed)"
  if command -v ufw >/dev/null 2>&1; then
    # Не включаем ufw принудительно — только добавляем правила, если ufw используется.
    sudo ufw allow 22/tcp || true
    sudo ufw allow "${SIGNALING_PORT}/tcp" || true
    sudo ufw allow 3478/udp || true
    sudo ufw allow 49152:49200/udp || true
    sudo ufw reload || true
  else
    echo "[remote] ufw not installed, skipping"
  fi
fi

echo "[remote] docker compose up"
sudo docker compose up -d --build

echo "[remote] docker compose ps"
sudo docker compose ps

echo "[remote] healthz"
curl -fsS "http://127.0.0.1:${SIGNALING_PORT}/healthz" >/dev/null
echo "OK"

echo "API:  http://${PUBLIC_IPV4}:${SIGNALING_PORT}"
echo "WS:   ws://${PUBLIC_IPV4}:${SIGNALING_PORT}/ws"
echo "STUN: stun:${PUBLIC_IPV4}:3478"
echo "TURN: turn:${PUBLIC_IPV4}:3478?transport=udp"
'@

$remoteScript = $remoteScript.Replace("__REMOTE_DIR__", $bRemoteDir).
  Replace("__REMOTE_TAR__", $bRemoteTmp).
  Replace("__SIGNALING_PORT__", $bSignalingPort).
  Replace("__TURN_REALM__", $bTurnRealm).
  Replace("__TURN_USER__", $bTurnUser).
  Replace("__TURN_PASS__", $bTurnPass).
  Replace("__PUBLIC_IPV4__", $bPublicIpv4).
  Replace("__OPEN_UFW__", $bOpenUfw)

# Avoid PowerShell parsing/quoting hell: send bash script via STDIN.
$remoteScript = $remoteScript -replace "`r`n", "`n"
$tmpRemoteScript = Join-Path $tmp ("zconect-remote-" + $stamp + ".sh")
Set-Content -Path $tmpRemoteScript -Value $remoteScript -Encoding UTF8 -NoNewline
$stdin = Get-Content -Path $tmpRemoteScript -Raw
$stdin | & ssh @sshArgs $remoteHost "bash -s"
if ($LASTEXITCODE -ne 0) {
  throw "Remote bootstrap failed with exit code $LASTEXITCODE"
}

Write-Host "[4/5] Done." -ForegroundColor Green
Write-Host "Client settings:" -ForegroundColor Green
Write-Host "  Server API URL: http://$PublicIpv4`:$SignalingPort" -ForegroundColor Green
Write-Host "  WebSocket URL:  ws://$PublicIpv4`:$SignalingPort/ws" -ForegroundColor Green
Write-Host "  STUN URL:       stun:$PublicIpv4`:3478" -ForegroundColor Green
Write-Host "  TURN URL:       turn:$PublicIpv4`:3478?transport=udp" -ForegroundColor Green

Write-Host "[5/5] Logs on VPS:" -ForegroundColor DarkGray
Write-Host "  ssh $remoteHost" -ForegroundColor DarkGray
Write-Host "  cd $RemoteDir/deploy && sudo docker compose logs -f signaling" -ForegroundColor DarkGray

