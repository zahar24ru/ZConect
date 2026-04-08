<#
.SYNOPSIS
  Деплой ZConect на Ubuntu VPS с Windows-машины.

.DESCRIPTION
  Полный цикл без GitHub и без компиляции на сервере:
  1) Собирает Docker-образ signaling-сервера локально (docker build)
  2) Сохраняет образ в сжатый файл (docker save | gzip)
  3) Загружает образ + deploy/ на VPS по scp
  4) По ssh:
     - ставит Docker + Compose если нет
     - загружает образ (docker load)
     - пишет deploy/.env
     - (опционально) открывает ufw-порты
     - docker compose up -d

  Требования на Windows: Docker Desktop (запущен), ssh, scp, tar.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\deploy_from_windows.ps1 `
    -VpsIp 93.115.203.200 -SshUser root -TurnPass "my_pass" -PublicIpv4 93.115.203.200
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

# ── Определяем пути ──────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
  $scriptPath = $MyInvocation.MyCommand.Path
  if ([string]::IsNullOrWhiteSpace($scriptPath)) {
    throw "Не удалось определить путь скрипта. Передайте -ProjectPath явно."
  }
  $scriptDir  = Split-Path -Parent $scriptPath
  $ProjectPath = (Resolve-Path (Join-Path $scriptDir "..")).Path
}

$ServerDir = Join-Path $ProjectPath "server"
$DeployDir = Join-Path $ProjectPath "deploy"

if (-not (Test-Path $ServerDir)) { throw "Папка server/ не найдена: $ServerDir" }
if (-not (Test-Path $DeployDir)) { throw "Папка deploy/ не найдена: $DeployDir" }

function Require-Command([string]$Name) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "Команда не найдена: $Name. Установите её и повторите."
  }
}

function New-SshArgs() {
  $a = @("-o", "StrictHostKeyChecking=accept-new")
  if ($SshKeyPath -and (Test-Path $SshKeyPath)) { $a += @("-i", $SshKeyPath) }
  return ,$a
}

function Bash-SingleQuote([string]$s) {
  return "'" + ($s -replace "'", "'""'""'") + "'"
}

Require-Command "ssh"
Require-Command "scp"
Require-Command "docker"

$remoteHost = "$SshUser@$VpsIp"
$sshArgs    = New-SshArgs
$tmp        = [System.IO.Path]::GetTempPath()
$stamp      = Get-Date -Format "yyyyMMdd-HHmmss"
$imageName  = "zconect-signaling:latest"
$imageTar   = Join-Path $tmp "zconect-signaling-$stamp.tar.gz"
$deployTar  = Join-Path $tmp "zconect-deploy-$stamp.tar.gz"

# ── Этап 1: сборка образа локально ───────────────────────────────────────────
Write-Host "[1/6] Building Docker image ($imageName)..." -ForegroundColor Cyan
Write-Host "  Context: $ServerDir" -ForegroundColor DarkGray

& docker build -t $imageName $ServerDir
if ($LASTEXITCODE -ne 0) { throw "docker build завершился с ошибкой $LASTEXITCODE" }

# ── Этап 2: сохранение образа в файл ─────────────────────────────────────────
Write-Host "[2/6] Saving image to $imageTar ..." -ForegroundColor Cyan

# docker save выводит tar в stdout → сжимаем через GZipStream
$rawTar = Join-Path $tmp "zconect-signaling-$stamp.tar"
& docker save -o $rawTar $imageName
if ($LASTEXITCODE -ne 0) { throw "docker save завершился с ошибкой $LASTEXITCODE" }

Write-Host "  Compressing..." -ForegroundColor DarkGray
$inStream  = [System.IO.File]::OpenRead($rawTar)
$outStream = [System.IO.File]::Create($imageTar)
$gzip      = [System.IO.Compression.GZipStream]::new($outStream, [System.IO.Compression.CompressionMode]::Compress)
$inStream.CopyTo($gzip)
$gzip.Dispose(); $outStream.Dispose(); $inStream.Dispose()
Remove-Item $rawTar -ErrorAction SilentlyContinue

$sizeMb = [math]::Round((Get-Item $imageTar).Length / 1MB, 1)
Write-Host "  Размер образа: $sizeMb МБ" -ForegroundColor DarkGray

# ── Этап 3: упаковка deploy/ ─────────────────────────────────────────────────
Write-Host "[3/6] Packing deploy/..." -ForegroundColor Cyan
Push-Location $ProjectPath
try {
  & tar -czf $deployTar "deploy"
} finally {
  Pop-Location
}

# ── Этап 4: загрузка файлов на VPS ───────────────────────────────────────────
Write-Host "[4/6] Uploading to VPS ($VpsIp)..." -ForegroundColor Cyan

$remoteImageTar  = "/tmp/zconect-signaling-$stamp.tar.gz"
$remoteDeployTar = "/tmp/zconect-deploy-$stamp.tar.gz"

Write-Host "  Uploading image ($sizeMb МБ)..." -ForegroundColor DarkGray
& scp @sshArgs $imageTar  "$remoteHost`:$remoteImageTar"

Write-Host "  Uploading deploy/..." -ForegroundColor DarkGray
& scp @sshArgs $deployTar "$remoteHost`:$remoteDeployTar"

# ── Этап 5: удалённый bootstrap ──────────────────────────────────────────────
Write-Host "[5/6] Running remote bootstrap..." -ForegroundColor Cyan

$openUfw      = if ($OpenUfwPorts.IsPresent) { "1" } else { "0" }
$bRemoteDir   = Bash-SingleQuote $RemoteDir
$bImageTar    = Bash-SingleQuote $remoteImageTar
$bDeployTar   = Bash-SingleQuote $remoteDeployTar
$bPort        = Bash-SingleQuote ([string]$SignalingPort)
$bTurnRealm   = Bash-SingleQuote $TurnRealm
$bTurnUser    = Bash-SingleQuote $TurnUser
$bTurnPass    = Bash-SingleQuote $TurnPass
$bPublicIpv4  = Bash-SingleQuote $PublicIpv4
$bOpenUfw     = Bash-SingleQuote $openUfw

$remoteScript = @'
set -euo pipefail

REMOTE_DIR=__REMOTE_DIR__
IMAGE_TAR=__IMAGE_TAR__
DEPLOY_TAR=__DEPLOY_TAR__
SIGNALING_PORT=__SIGNALING_PORT__
TURN_REALM=__TURN_REALM__
TURN_USER=__TURN_USER__
TURN_PASS=__TURN_PASS__
PUBLIC_IPV4=__PUBLIC_IPV4__
OPEN_UFW=__OPEN_UFW__

echo "[remote] preparing directory $REMOTE_DIR"
sudo mkdir -p "$REMOTE_DIR"
sudo chown -R $(id -u):$(id -g) "$REMOTE_DIR"

echo "[remote] checking Docker"
if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  echo "[remote] installing Docker CE from official repo"
  sudo apt-get update -y -qq
  sudo apt-get install -y ca-certificates curl gnupg
  sudo install -m 0755 -d /etc/apt/keyrings
  if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    sudo chmod a+r /etc/apt/keyrings/docker.gpg
  fi
  . /etc/os-release
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -y
  sudo apt-get remove -y docker.io docker-doc docker-compose docker-compose-v2 \
    podman-docker containerd runc 2>/dev/null || true
  sudo apt-get install -y docker-ce docker-ce-cli containerd.io \
    docker-buildx-plugin docker-compose-plugin
fi

sudo systemctl enable docker.socket >/dev/null 2>&1 || true
sudo systemctl start docker.socket  >/dev/null 2>&1 || true
sudo systemctl enable --now docker   >/dev/null
docker --version
docker compose version

echo "[remote] loading Docker image (docker load)"
sudo docker load < "$IMAGE_TAR"
echo "[remote] image loaded"

echo "[remote] extracting deploy/"
tar -xzf "$DEPLOY_TAR" -C "$REMOTE_DIR"

echo "[remote] writing deploy/.env"
cd "$REMOTE_DIR/deploy"
cat > .env <<EOF
APP_ENV=prod
SIGNALING_PORT=$SIGNALING_PORT
SESSION_TTL_SEC=300
MAX_JOIN_ATTEMPTS=5
LOCK_MINUTES=10
TURN_REALM=$TURN_REALM
TURN_USER=$TURN_USER
TURN_PASS=$TURN_PASS
PUBLIC_IPV4=$PUBLIC_IPV4
ALLOWED_ORIGINS=
EOF

if [ "$OPEN_UFW" = "1" ]; then
  if command -v ufw >/dev/null 2>&1; then
    echo "[remote] opening ufw ports"
    sudo ufw allow 22/tcp    || true
    sudo ufw allow "${SIGNALING_PORT}/tcp" || true
    sudo ufw allow 3478/udp  || true
    sudo ufw allow 49152:49200/udp || true
    sudo ufw reload          || true
  fi
fi

echo "[remote] docker compose up"
sudo docker compose up -d

echo "[remote] docker compose ps"
sudo docker compose ps

echo "[remote] healthcheck"
for i in $(seq 1 20); do
  if curl -fsS "http://127.0.0.1:${SIGNALING_PORT}/healthz" >/dev/null 2>&1; then
    echo "healthz OK"
    break
  fi
  sleep 1
done

echo "API:  http://${PUBLIC_IPV4}:${SIGNALING_PORT}"
echo "WS:   ws://${PUBLIC_IPV4}:${SIGNALING_PORT}/ws"
echo "STUN: stun:${PUBLIC_IPV4}:3478"
echo "TURN: turn:${PUBLIC_IPV4}:3478?transport=udp"

echo "[remote] cleaning up temp files"
rm -f "$IMAGE_TAR" "$DEPLOY_TAR"
'@

$remoteScript = $remoteScript.
  Replace("__REMOTE_DIR__",     $bRemoteDir).
  Replace("__IMAGE_TAR__",      $bImageTar).
  Replace("__DEPLOY_TAR__",     $bDeployTar).
  Replace("__SIGNALING_PORT__", $bPort).
  Replace("__TURN_REALM__",     $bTurnRealm).
  Replace("__TURN_USER__",      $bTurnUser).
  Replace("__TURN_PASS__",      $bTurnPass).
  Replace("__PUBLIC_IPV4__",    $bPublicIpv4).
  Replace("__OPEN_UFW__",       $bOpenUfw)

$remoteScript = $remoteScript -replace "`r`n", "`n"
$tmpScript = Join-Path $tmp "zconect-remote-$stamp.sh"
Set-Content -Path $tmpScript -Value $remoteScript -Encoding UTF8 -NoNewline
(Get-Content -Path $tmpScript -Raw) | & ssh @sshArgs $remoteHost "bash -s"
if ($LASTEXITCODE -ne 0) { throw "Remote bootstrap завершился с ошибкой $LASTEXITCODE" }

# ── Этап 6: очистка локальных временных файлов ───────────────────────────────
Write-Host "[6/6] Cleanup..." -ForegroundColor Cyan
Remove-Item $imageTar  -ErrorAction SilentlyContinue
Remove-Item $deployTar -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Деплой завершён успешно." -ForegroundColor Green
Write-Host "Настройки клиента ZConnect:" -ForegroundColor Green
Write-Host "  Server API URL: http://$PublicIpv4`:$SignalingPort" -ForegroundColor Yellow
Write-Host "  WebSocket URL:  ws://$PublicIpv4`:$SignalingPort/ws" -ForegroundColor Yellow
Write-Host "  STUN URL:       stun:$PublicIpv4`:3478" -ForegroundColor Yellow
Write-Host "  TURN URL:       turn:$PublicIpv4`:3478?transport=udp" -ForegroundColor Yellow
Write-Host ""
Write-Host "Логи на VPS:" -ForegroundColor DarkGray
Write-Host "  ssh $remoteHost" -ForegroundColor DarkGray
Write-Host "  cd $RemoteDir/deploy && sudo docker compose logs -f signaling" -ForegroundColor DarkGray
