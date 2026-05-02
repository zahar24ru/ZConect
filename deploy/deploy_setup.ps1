<#
.SYNOPSIS
  Первоначальная настройка ZConect на Ubuntu VPS (ONE-TIME).

.DESCRIPTION
  Быстрый setup для fast-deploy workflow. Запускается ОДИН РАЗ при первой
  установке. Для последующих обновлений используется deploy_fast.ps1
  (только binary + restart, 10-20 секунд).

  Что делает:
   1) Устанавливает Docker CE (если нет)
   2) Создаёт /opt/zconect/{bin,deploy} на VPS
   3) Cross-compile Go binary на Windows (первый раз)
   4) Uploads: binary, docker-compose.fast.yml, .env
   5) Pulls alpine:3.21 + coturn образы
   6) Starts containers
   7) Открывает UFW ports (optional)

  После setup'а:
   - DB, client-version.json, admin-audit.log хранятся в signaling-data Docker volume
   - .env на сервере НЕ перезаписывается при fast deploy
   - Binary в /opt/zconect/deploy/bin/signaling заменяется только через deploy_fast.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
    -VpsIp YOUR_SERVER_IP -SshUser root -TurnPass "StrongPass123" -PublicIpv4 YOUR_SERVER_IP

  # С admin panel password (bcrypt hash):
  powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
    -VpsIp YOUR_SERVER_IP -SshUser root -TurnPass "StrongPass123" -PublicIpv4 YOUR_SERVER_IP `
    -AdminPasswordHash '$2a$12$...'
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$VpsIp,
  [Parameter()][string]$SshUser = "root",
  [Parameter()][string]$SshKeyPath = "",
  [Parameter()][string]$ProjectPath = "",
  [Parameter()][int]$SignalingPort = 8080,
  [Parameter()][string]$TurnRealm = "zconect.local",
  [Parameter()][string]$PublicIpv4 = "",    # опционально если .env уже есть
  [Parameter()][string]$AdminPasswordHash = "",
  [Parameter()][string]$AdminAllowedIPs = "",
  [Parameter()][string]$RemoteDir = "/opt/zconect",
  [Parameter()][switch]$OpenUfwPorts = $true,
  [Parameter()][switch]$ForceResetEnv,     # если set — .env перезаписывается полностью
  # ─── HTTPS (Caddy + Let's Encrypt) ───────────────────────────────────────
  # Если -Domain задан: Caddy получит TLS cert через ACME HTTP-01 challenge.
  # Requires: DNS A-record $Domain → $VpsIp UP и prop'нут (проверяется перед start).
  # -AdminEmail передаётся в Let's Encrypt для notifications об expiring certs.
  # Если -Domain пуст — HTTPS отключен, signaling доступен только по IP:PORT (HTTP).
  [Parameter()][string]$Domain = "",
  [Parameter()][string]$AdminEmail = "",
  # ─── TURN rotating credentials (RFC 7635) ────────────────────────────────
  # Single shared secret, coturn валидирует HMAC. Клиенты получают per-session
  # short-term creds через /api/v1/session/create|join|refresh response'ы.
  # Если пусто — auto-generated 32-byte hex secret (64 chars). Сохраняется в .env.
  # Менять secret можно через -ForceResetEnv либо ручной правкой .env (тогда все
  # активные clients перестанут auth'иться в coturn — нужен reconnect).
  # TurnCredTtlSec: время жизни каждого client credential (default 1800 = 30 мин).
  # Клиент должен refresh session (и получить новый cred) до expiry.
  [Parameter()][string]$TurnAuthSecret = "",
  [Parameter()][int]$TurnCredTtlSec = 1800
)

# Валидация: если .env ещё НЕТ на сервере, TurnPass+PublicIpv4 обязательны.
# Проверяем это в remote script.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Input validation (ASCII-only throw messages for PowerShell 5.1 cp1251 compat)
# Caddyfile is generated via raw string replace __SIGNALING_DOMAIN__/__ADMIN_EMAIL__.
# If Domain or AdminEmail contain newline/special chars (Caddy control: { } # newline),
# attacker can inject rogue site blocks, directory browsing, reverse_proxy to evil backend.
# Validate params BEFORE they hit Caddyfile / .env / bash heredoc.
if (-not [string]::IsNullOrEmpty($Domain)) {
    if ($Domain.Length -gt 253) {
        throw "Domain too long (>253 chars): $($Domain.Length)"
    }
    if ($Domain -notmatch '^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,63}$') {
        throw "Domain invalid: '$Domain'. Expected FQDN like 'connect.example.com' (ASCII letters/digits/dots/hyphens; TLD 2-63 letters)."
    }
}
if (-not [string]::IsNullOrEmpty($AdminEmail)) {
    if ($AdminEmail.Length -gt 254) {
        throw "AdminEmail too long"
    }
    # Simplified RFC 5322 - matches 99% of valid emails, rejects spaces/newlines/quotes
    if ($AdminEmail -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._+-]*@[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$') {
        throw "AdminEmail invalid: '$AdminEmail'. Expected format user@example.com"
    }
}
# TurnAuthSecret - hex chars only when passed explicitly (auto-gen uses openssl rand -hex 32)
if (-not [string]::IsNullOrEmpty($TurnAuthSecret)) {
    if ($TurnAuthSecret -notmatch '^[0-9a-fA-F]{32,128}$') {
        throw "TurnAuthSecret must be hex string 32-128 chars (generated via 'openssl rand -hex 32')"
    }
}
# AdminPasswordHash - bcrypt format $2a$/$2b$/$2y$, content is base64 alphabet
if (-not [string]::IsNullOrEmpty($AdminPasswordHash)) {
    # Strip CR/LF in case of copy-paste contamination
    $AdminPasswordHash = $AdminPasswordHash -replace "[\r\n]", ""
    if ($AdminPasswordHash -notmatch '^\$2[aby]\$[0-9]{2}\$[./A-Za-z0-9]{53}$') {
        throw "AdminPasswordHash invalid bcrypt. Expected ~60-char format '`$2a`$12`$...' (generate via ./server/admin-hashpass.exe)"
    }
}

# ── Paths ────────────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
  $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
  $ProjectPath = (Resolve-Path (Join-Path $scriptDir "..")).Path
}
$ServerDir = Join-Path $ProjectPath "server"
$DeployDir = Join-Path $ProjectPath "deploy"
$BinDir    = Join-Path $DeployDir "bin"
$BinaryPath = Join-Path $BinDir "signaling"

if (-not (Test-Path $ServerDir)) { throw "server/ не найдена: $ServerDir" }

function Require-Command([string]$Name) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "Команда не найдена: $Name. Установите её."
  }
}
Require-Command "ssh"
Require-Command "scp"
Require-Command "go"

$remoteHost = "$SshUser@$VpsIp"
$sshArgs = @("-o", "StrictHostKeyChecking=accept-new")
if ($SshKeyPath -and (Test-Path $SshKeyPath)) { $sshArgs += @("-i", $SshKeyPath) }

# ── 1. Cross-compile Go binary for Linux ────────────────────────────────
Write-Host "[1/5] Cross-compiling signaling (GOOS=linux GOARCH=amd64)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $BinDir | Out-Null

$env:GOOS = "linux"
$env:GOARCH = "amd64"
$env:CGO_ENABLED = "0"

Push-Location $ServerDir
try {
  # Tidy deps first (нужно для bcrypt если go.mod ещё не обновлён).
  & go mod tidy
  if ($LASTEXITCODE -ne 0) { throw "go mod tidy failed" }

  & go build -trimpath -ldflags="-s -w" -o $BinaryPath ./cmd/signaling
  if ($LASTEXITCODE -ne 0) { throw "go build failed" }
}
finally {
  Pop-Location
  # Reset env vars
  $env:GOOS = ""
  $env:GOARCH = ""
  $env:CGO_ENABLED = ""
}

$binSize = [math]::Round((Get-Item $BinaryPath).Length / 1MB, 1)
Write-Host "  Binary: $binSize МБ" -ForegroundColor DarkGray

# ── 2. Remote setup script (install Docker, create dirs, write .env) ────
Write-Host "[2/5] Installing Docker + creating directories on VPS..." -ForegroundColor Cyan

$openUfw = if ($OpenUfwPorts.IsPresent) { "1" } else { "0" }
$forceReset = if ($ForceResetEnv.IsPresent) { "1" } else { "0" }

# Auto-generate TURN_AUTH_SECRET if empty. 32 bytes → 64 hex chars.
# Если .env уже содержит secret — remote script подхватит его (smart-merge).
# Новое значение генерируется только когда:
#   (a) CLI пустое И
#   (b) env_get в remote вернёт пустое (первый запуск или -ForceResetEnv)
# Если пропускаем генерацию здесь, в remote script'е проверка pick'а подхватит.
if ([string]::IsNullOrWhiteSpace($TurnAuthSecret)) {
    # Оставляем пустым; remote script сам сгенерит ЕСЛИ .env не содержит
    Write-Host "  TURN_AUTH_SECRET будет автогенерирован на сервере если не задан в .env" -ForegroundColor DarkGray
}

function Bash-SingleQuote([string]$s) {
  return "'" + ($s -replace "'", "'""'""'") + "'"
}

$bRemoteDir = Bash-SingleQuote $RemoteDir
$bPort = Bash-SingleQuote ([string]$SignalingPort)
$bRealm = Bash-SingleQuote $TurnRealm
$bPubIpv4 = Bash-SingleQuote $PublicIpv4
$bAdminHash = Bash-SingleQuote $AdminPasswordHash
$bAdminIps = Bash-SingleQuote $AdminAllowedIPs
$bOpenUfw = Bash-SingleQuote $openUfw
$bForceReset = Bash-SingleQuote $forceReset
$bDomain = Bash-SingleQuote $Domain
$bAdminEmail = Bash-SingleQuote $AdminEmail
$bTurnSecret = Bash-SingleQuote $TurnAuthSecret
$bTurnTTL = Bash-SingleQuote ([string]$TurnCredTtlSec)

$setupScript = @'
set -euo pipefail
REMOTE_DIR=__REMOTE_DIR__
SIGNALING_PORT=__SIGNALING_PORT__
TURN_REALM=__TURN_REALM__
PUBLIC_IPV4=__PUBLIC_IPV4__
ADMIN_PASSWORD_HASH=__ADMIN_PASSWORD_HASH__
ADMIN_ALLOWED_IPS=__ADMIN_ALLOWED_IPS__
OPEN_UFW=__OPEN_UFW__
FORCE_RESET=__FORCE_RESET__
SIGNALING_DOMAIN=__DOMAIN__
ADMIN_EMAIL=__ADMIN_EMAIL__
TURN_AUTH_SECRET=__TURN_AUTH_SECRET__
TURN_CRED_TTL_SEC=__TURN_CRED_TTL_SEC__

echo "[remote] preparing $REMOTE_DIR"
sudo mkdir -p "$REMOTE_DIR"/deploy/bin
sudo chown -R $(id -u):$(id -g) "$REMOTE_DIR"

echo "[remote] checking Docker"
if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  echo "[remote] installing Docker CE"
  sudo apt-get update -y -qq
  sudo apt-get install -y ca-certificates curl gnupg
  sudo install -m 0755 -d /etc/apt/keyrings
  if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    sudo chmod a+r /etc/apt/keyrings/docker.gpg
  fi
  . /etc/os-release
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -y
  sudo apt-get remove -y docker.io docker-doc docker-compose docker-compose-v2 podman-docker containerd runc 2>/dev/null || true
  sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
sudo systemctl enable --now docker >/dev/null 2>&1 || true
docker --version
docker compose version

# ── .env handling — SMART MERGE вместо overwrite ──────────────────────
# Поведение:
#  - Если .env НЕ существует → создаём новый (TurnPass и PublicIpv4 обязательны)
#  - Если .env существует и -ForceResetEnv НЕ указан → merge: обновляем только
#    те поля которые явно переданы через CLI, остальное сохраняется
#  - Если -ForceResetEnv указан → перезаписываем (старое бэкапится в .env.backup)

ENV_FILE="$REMOTE_DIR/deploy/.env"
ENV_EXISTS=0
if [ -f "$ENV_FILE" ]; then ENV_EXISTS=1; fi

# Helper: get existing value from .env или fallback.
# Unescape `$$` → `$` после чтения — внутренние вычисления работают с "сырыми"
# значениями, escaping применяется только при write (см. cat > .env EOF ниже).
env_get() {
  local key="$1"
  local fallback="$2"
  if [ "$ENV_EXISTS" = "1" ]; then
    local val
    val=$(grep "^${key}=" "$ENV_FILE" | head -1 | sed "s/^${key}=//") || true
    if [ -n "$val" ]; then
      # Unescape $$ → $
      echo "$val" | sed 's/\$\$/\$/g'
      return
    fi
  fi
  echo "$fallback"
}

# Helper: если CLI value непустое — использовать его, иначе взять из .env, иначе fallback
pick() {
  local cli="$1"
  local env_key="$2"
  local fallback="$3"
  if [ -n "$cli" ]; then
    echo "$cli"
  else
    env_get "$env_key" "$fallback"
  fi
}

if [ "$ENV_EXISTS" = "1" ] && [ "$FORCE_RESET" != "1" ]; then
  echo "[remote] .env exists — smart merge (используй -ForceResetEnv для полной перезаписи)"
  cp "$ENV_FILE" "$ENV_FILE.backup-$(date +%Y%m%d-%H%M%S)"
else
  if [ "$ENV_EXISTS" = "0" ]; then
    echo "[remote] .env не найден — создаём новый"
    if [ -z "$PUBLIC_IPV4" ]; then
      echo "ERROR: .env не существует, -PublicIpv4 обязателен при первом setup"
      exit 2
    fi
  else
    echo "[remote] .env существует + -ForceResetEnv → полная перезапись (бэкап в .env.backup)"
    cp "$ENV_FILE" "$ENV_FILE.backup-$(date +%Y%m%d-%H%M%S)"
  fi
fi

# Resolve все значения — CLI > existing .env > defaults
FINAL_SIGNALING_PORT=$(pick "$SIGNALING_PORT" "SIGNALING_PORT" "8080")
FINAL_TURN_REALM=$(pick "$TURN_REALM" "TURN_REALM" "zconect.local")
FINAL_PUBLIC_IPV4=$(pick "$PUBLIC_IPV4" "PUBLIC_IPV4" "")
FINAL_ADMIN_HASH=$(pick "$ADMIN_PASSWORD_HASH" "ADMIN_PASSWORD_HASH" "")
FINAL_ADMIN_IPS=$(pick "$ADMIN_ALLOWED_IPS" "ADMIN_ALLOWED_IPS" "")

# TURN rotating auth — secret auto-generated если пусто в .env И не передано через CLI.
FINAL_TURN_AUTH_SECRET=$(pick "$TURN_AUTH_SECRET" "TURN_AUTH_SECRET" "")
if [ -z "$FINAL_TURN_AUTH_SECRET" ]; then
  # Auto-generate 32 bytes → 64 hex chars. openssl у Ubuntu всегда есть.
  FINAL_TURN_AUTH_SECRET=$(openssl rand -hex 32)
  echo "[remote] TURN_AUTH_SECRET auto-generated (32 bytes hex)"
fi
FINAL_TURN_CRED_TTL=$(pick "$TURN_CRED_TTL_SEC" "TURN_CRED_TTL_SEC" "1800")
# Preserve остальные поля из старого .env
FINAL_APP_ENV=$(env_get "APP_ENV" "prod")
FINAL_SESSION_TTL=$(env_get "SESSION_TTL_SEC" "300")
FINAL_MAX_JOIN=$(env_get "MAX_JOIN_ATTEMPTS" "5")
FINAL_LOCK=$(env_get "LOCK_MINUTES" "10")
FINAL_RATE=$(env_get "RATE_LIMIT_PER_MIN" "200")
FINAL_ALLOWED_ORIGINS=$(env_get "ALLOWED_ORIGINS" "")
FINAL_TRUSTED_PROXIES=$(env_get "TRUSTED_PROXIES" "")
FINAL_DASHBOARD_KEY=$(env_get "DASHBOARD_API_KEY" "")
FINAL_ADMIN_IDLE=$(env_get "ADMIN_SESSION_IDLE_TTL_SEC" "3600")
FINAL_ADMIN_MAX=$(env_get "ADMIN_SESSION_MAX_LIFE_SEC" "28800")
FINAL_ADMIN_COOKIE=$(env_get "ADMIN_COOKIE_SECURE" "false")
FINAL_ADMIN_HTTP=$(env_get "ADMIN_ALLOW_HTTP_DOWNLOAD" "0")
FINAL_SIGNALING_DOMAIN=$(pick "$SIGNALING_DOMAIN" "SIGNALING_DOMAIN" "")
FINAL_ADMIN_EMAIL=$(pick "$ADMIN_EMAIL" "ADMIN_EMAIL" "")

# HTTPS mode: если DOMAIN задан → forcibly set ADMIN_COOKIE_SECURE=true
# (в prod за Caddy TLS cookie без Secure flag небезопасен)
if [ -n "$FINAL_SIGNALING_DOMAIN" ]; then
  FINAL_ADMIN_COOKIE="true"
  echo "[remote] HTTPS mode detected (DOMAIN=$FINAL_SIGNALING_DOMAIN) — forcing ADMIN_COOKIE_SECURE=true"
fi

# Validate mandatory
if [ -z "$FINAL_PUBLIC_IPV4" ]; then
  echo "ERROR: PUBLIC_IPV4 не задан (ни в .env, ни в CLI)"
  exit 2
fi
if [ -z "$FINAL_TURN_AUTH_SECRET" ]; then
  echo "ERROR: TURN_AUTH_SECRET не задан и openssl не сгенерил. Проверь наличие openssl."
  exit 2
fi

# Escape `$` → `$$` для docker-compose .env interpolation.
# Иначе bcrypt hash $2a$12$... compose видит как $2a, $12, $hash ссылки
# на несуществующие переменные → warnings + empty value в контейнере.
# $$ экранирует символ; docker-compose distrubute'ит `$` в значение at runtime.
#
# TURN_AUTH_SECRET — hex string (0-9a-f), там нет `$`, escape не нужен, но
# делаем для надёжности (на случай если user вручную вставит с $ в secret).
escape_env() {
  echo "$1" | sed 's/\$/\$\$/g'
}
ESC_ADMIN_HASH=$(escape_env "$FINAL_ADMIN_HASH")
ESC_TURN_SECRET=$(escape_env "$FINAL_TURN_AUTH_SECRET")

# TURN_PUBLIC_HOST — для client ICE config. Используем домен (если HTTPS mode)
# или PublicIpv4 (HTTP mode). Клиент подключается к TURN по имени/IP на порт 3478.
# Note: TURN не за Caddy'ем — coturn сам слушает 3478 на VPS.
FINAL_TURN_PUBLIC_HOST=$(env_get "TURN_PUBLIC_HOST" "$FINAL_PUBLIC_IPV4")

echo "[remote] writing .env (CLI params override existing values; \$ escaped as \$\$)"
# Umask 0077 для следующего cat > .env — result: 0600 (owner read/write only).
# Файл содержит TURN_AUTH_SECRET (HMAC secret — если leak, все TURN creds forgeable)
# и ADMIN_PASSWORD_HASH (bcrypt — offline crackable). Default umask 0022 создал бы
# 0644 world-readable — любой local process (cron/logshipper/другой user) мог бы прочесть.
(umask 077
cat > "$ENV_FILE" <<EOF
APP_ENV=$FINAL_APP_ENV
SIGNALING_PORT=$FINAL_SIGNALING_PORT
SESSION_TTL_SEC=$FINAL_SESSION_TTL
MAX_JOIN_ATTEMPTS=$FINAL_MAX_JOIN
LOCK_MINUTES=$FINAL_LOCK
RATE_LIMIT_PER_MIN=$FINAL_RATE
ALLOWED_ORIGINS=$FINAL_ALLOWED_ORIGINS
TRUSTED_PROXIES=$FINAL_TRUSTED_PROXIES
# Legacy dashboard (DEPRECATED — заменён admin panel):
DASHBOARD_API_KEY=$FINAL_DASHBOARD_KEY
# TURN rotating credentials (RFC 7635 / coturn --use-auth-secret):
#   - TURN_AUTH_SECRET: shared HMAC secret (НЕ менять часто — все активные clients перестанут auth'иться)
#   - TURN_CRED_TTL_SEC: время жизни per-client credential (default 1800 = 30 мин)
#   - TURN_PUBLIC_HOST: host/IP для client ICE config (выставляется в turn_servers response'e)
TURN_REALM=$FINAL_TURN_REALM
TURN_AUTH_SECRET=$ESC_TURN_SECRET
TURN_CRED_TTL_SEC=$FINAL_TURN_CRED_TTL
TURN_PUBLIC_HOST=$FINAL_TURN_PUBLIC_HOST
TURN_PUBLIC_PORT=3478
PUBLIC_IPV4=$FINAL_PUBLIC_IPV4
# Admin panel (bcrypt hash escaped \$ → \$\$ для docker-compose):
ADMIN_PASSWORD_HASH=$ESC_ADMIN_HASH
ADMIN_ALLOWED_IPS=$FINAL_ADMIN_IPS
ADMIN_SESSION_IDLE_TTL_SEC=$FINAL_ADMIN_IDLE
ADMIN_SESSION_MAX_LIFE_SEC=$FINAL_ADMIN_MAX
ADMIN_COOKIE_SECURE=$FINAL_ADMIN_COOKIE
ADMIN_ALLOW_HTTP_DOWNLOAD=$FINAL_ADMIN_HTTP
# HTTPS — Caddy reverse proxy (empty SIGNALING_DOMAIN = HTTP mode, Caddy отключен):
SIGNALING_DOMAIN=$FINAL_SIGNALING_DOMAIN
ADMIN_EMAIL=$FINAL_ADMIN_EMAIL
EOF
)
# Доп. явный chmod для backup'ов которые были созданы раньше с umask 0022.
# Новые backup'ы создаются через cp с source .env (0600) → inherit permissions.
chmod 600 "$ENV_FILE" 2>/dev/null || true
# Также backup'ы .env.backup-* — они создавались через cp до того как мы дошли
# сюда, могли унаследовать 0644. Сейчас уже не критично (если 0600 установили
# после), но для старых backup'ов ставим 0600 явно.
find "$REMOTE_DIR/deploy" -maxdepth 1 -name ".env.backup-*" -exec chmod 600 {} \; 2>/dev/null || true
echo "[remote] .env written (mode 0600) — backup saved as .env.backup-*"

if [ "$OPEN_UFW" = "1" ]; then
  if command -v ufw >/dev/null 2>&1; then
    echo "[remote] opening UFW ports"
    sudo ufw allow 22/tcp || true
    # HTTP/HTTPS — нужны Caddy для ACME + HTTPS traffic. Signaling port в docker network only.
    sudo ufw allow 80/tcp || true
    sudo ufw allow 443/tcp || true
    sudo ufw allow 443/udp || true  # HTTP/3 (QUIC)
    # Fallback для HTTP mode (если DOMAIN не задан) — открываем signaling port наружу
    if [ -z "$FINAL_SIGNALING_DOMAIN" ]; then
      sudo ufw allow "${FINAL_SIGNALING_PORT}/tcp" || true
    fi
    sudo ufw allow 3478/udp || true
    sudo ufw allow 49152:49200/udp || true
    sudo ufw reload || true
  fi
fi

echo "[remote] setup done"
'@

$setupScript = $setupScript.
  Replace("__REMOTE_DIR__", $bRemoteDir).
  Replace("__SIGNALING_PORT__", $bPort).
  Replace("__TURN_REALM__", $bRealm).
  Replace("__PUBLIC_IPV4__", $bPubIpv4).
  Replace("__ADMIN_PASSWORD_HASH__", $bAdminHash).
  Replace("__ADMIN_ALLOWED_IPS__", $bAdminIps).
  Replace("__OPEN_UFW__", $bOpenUfw).
  Replace("__FORCE_RESET__", $bForceReset).
  Replace("__TURN_AUTH_SECRET__", $bTurnSecret).
  Replace("__TURN_CRED_TTL_SEC__", $bTurnTTL).
  Replace("__DOMAIN__", $bDomain).
  Replace("__ADMIN_EMAIL__", $bAdminEmail)

$setupScript = $setupScript -replace "`r`n", "`n"
$tmpScript = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tmpScript -Value $setupScript -Encoding UTF8 -NoNewline
# tr -d '\r' — strip carriage returns которые PowerShell добавляет при piping'е,
# bash не выполняет команды с \r в имени.
(Get-Content -Path $tmpScript -Raw) | & ssh @sshArgs $remoteHost "tr -d '\r' | bash"
if ($LASTEXITCODE -ne 0) { throw "Remote setup failed $LASTEXITCODE" }
Remove-Item $tmpScript -ErrorAction SilentlyContinue

# ── 3. Upload compose + binary ──────────────────────────────────────────
Write-Host "[3/5] Uploading docker-compose.fast.yml + binary..." -ForegroundColor Cyan

$composeFile = Join-Path $DeployDir "docker-compose.fast.yml"

# Backup old docker-compose.yml before replacement (если существует).
# ВАЖНО: single-quoted here-string @'...'@ — PowerShell НЕ интерпретирует $() внутри,
# иначе $(date +%Y%m%d) PS попытается выполнить как Get-Date -Date '+%Y%m%d' → error.
$backupScript = @'
set -euo pipefail
REMOTE_DIR=__REMOTE_DIR__
if [ -f "$REMOTE_DIR/deploy/docker-compose.yml" ]; then
  cp "$REMOTE_DIR/deploy/docker-compose.yml" "$REMOTE_DIR/deploy/docker-compose.yml.backup-$(date +%Y%m%d-%H%M%S)"
  echo 'old docker-compose.yml backed up'
fi
'@
$backupScript = $backupScript.Replace("__REMOTE_DIR__", $RemoteDir)
$backupScript = $backupScript -replace "`r`n", "`n"
$tmpBk = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tmpBk -Value $backupScript -Encoding UTF8 -NoNewline
(Get-Content -Path $tmpBk -Raw) | & ssh @sshArgs $remoteHost "tr -d '\r' | bash"
Remove-Item $tmpBk -ErrorAction SilentlyContinue

& scp @sshArgs $composeFile "$remoteHost`:$RemoteDir/deploy/docker-compose.yml"
if ($LASTEXITCODE -ne 0) { throw "scp compose failed" }

# Binary upload → сначала в .new, потом atomic swap на remote.
# Причина: если signaling container уже running, он держит binary как mmap'нутый
# executable; scp не может overwrite в-place. .new + mv — атомарно и всегда работает.
& scp @sshArgs $BinaryPath "$remoteHost`:$RemoteDir/deploy/bin/signaling.new"
if ($LASTEXITCODE -ne 0) { throw "scp binary failed" }

# Generate Caddyfile from template (substitute $Domain and $AdminEmail)
# и upload на сервер. Если Domain пуст — Caddyfile остаётся с placeholder'ом
# но Caddy будет крашиться пока не получит real domain. Альтернативно: если
# Domain пуст, upload'им "пустой" Caddyfile который не стартует Caddy.
$caddyfileTemplate = Join-Path $DeployDir "Caddyfile.template"
if (Test-Path $caddyfileTemplate) {
    $caddyfileContent = Get-Content -Path $caddyfileTemplate -Raw
    if ([string]::IsNullOrWhiteSpace($Domain)) {
        # HTTP mode fallback — Caddy тупо не заработает без domain, но compose
        # всё равно пытается его запустить. Пишем Caddyfile с пустым site block.
        # Fast deploy может позже обновить его.
        $caddyfileContent = "# HTTP-only mode — no domain set. Caddy disabled until -Domain provided.`n# Delete caddy-data volume + restart after setting domain.`n:80 {`n    respond `"ZConect signaling server (HTTP mode, HTTPS not configured)`" 200`n}`n"
        Write-Host "  (HTTP-only mode, no -Domain → Caddy placeholder Caddyfile)" -ForegroundColor Yellow
    } else {
        $emailForCaddy = if ([string]::IsNullOrWhiteSpace($AdminEmail)) { "noreply@${Domain}" } else { $AdminEmail }
        $caddyfileContent = $caddyfileContent.Replace("__SIGNALING_DOMAIN__", $Domain)
        $caddyfileContent = $caddyfileContent.Replace("__ADMIN_EMAIL__", $emailForCaddy)
        Write-Host "  Caddy config: $Domain → signaling:8080 (email=$emailForCaddy)" -ForegroundColor Cyan
    }
    $tmpCaddy = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmpCaddy -Value $caddyfileContent -Encoding UTF8 -NoNewline
    (Get-Content -Path $tmpCaddy -Raw) -replace "`r`n", "`n" | Set-Content -Path $tmpCaddy -Encoding UTF8 -NoNewline
    & scp @sshArgs $tmpCaddy "$remoteHost`:$RemoteDir/deploy/Caddyfile"
    Remove-Item $tmpCaddy -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { throw "scp Caddyfile failed" }
}

# ── Upload static landing page (deploy/public/) → /opt/zconect/deploy/public/ ─
# Caddy монтирует эту директорию как /srv (см. docker-compose.fast.yml) и serves
# её на /. В HTTP-only mode (no Domain) page недоступна — Caddy отдаёт placeholder.
#
# Security (audit M4): upload'им ЯВНО index.html + расширения которые реально
# используем, а не `public/*` glob. Это защищает от accidental leak'а dev-файлов
# (.env.local, notes.md, *.bak, .DS_Store) если кто-то положит их в public/.
$publicDir = Join-Path $DeployDir "public"
$indexHtml = Join-Path $publicDir "index.html"
if (Test-Path $indexHtml) {
    Write-Host "  Uploading static landing page (index.html)..." -ForegroundColor Cyan
    # Создаём директорию на сервере если нет (idempotent).
    $mkdirScript = "mkdir -p '$RemoteDir/deploy/public'"
    & ssh @sshArgs $remoteHost "$mkdirScript"
    & scp @sshArgs "$indexHtml" "$remoteHost`:$RemoteDir/deploy/public/index.html"
    if ($LASTEXITCODE -ne 0) { Write-Warning "scp index.html failed — landing page не будет работать" }
}

# ── 4. Start containers (atomic swap binary + compose up) ────────────────
Write-Host "[4/5] docker compose up -d (pulls alpine + coturn images)..." -ForegroundColor Cyan

$startScript = @'
set -euo pipefail
REMOTE_DIR=__REMOTE_DIR__
cd "$REMOTE_DIR/deploy"

# Stop signaling чтобы освободить mmap на binary — позволит atomic replace.
docker compose stop signaling 2>/dev/null || true

# Atomic swap: mv НЕ падает даже если target open — создаёт new inode.
if [ -f bin/signaling.new ]; then
  chmod +x bin/signaling.new
  mv bin/signaling.new bin/signaling
  echo "binary swapped"
fi
chmod +x bin/signaling

docker compose pull
docker compose up -d
sleep 1
docker compose ps
'@.Replace("__REMOTE_DIR__", $bRemoteDir)

$startScript = $startScript -replace "`r`n", "`n"
$tmpStart = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tmpStart -Value $startScript -Encoding UTF8 -NoNewline
(Get-Content -Path $tmpStart -Raw) | & ssh @sshArgs $remoteHost "tr -d '\r' | bash"
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }
Remove-Item $tmpStart -ErrorAction SilentlyContinue

# ── 5. Healthcheck ──────────────────────────────────────────────────────
Write-Host "[5/5] Healthcheck..." -ForegroundColor Cyan
Start-Sleep -Seconds 3

# Healthcheck URL: HTTPS если domain задан, иначе HTTP direct.
# При HTTPS mode даём больше времени (Caddy должен ACME challenge пройти ~10-30 сек).
$healthUrl = if ([string]::IsNullOrWhiteSpace($Domain)) {
    "http://${VpsIp}:${SignalingPort}/healthz"
} else {
    Write-Host "  Waiting 15s для Caddy Let's Encrypt handshake..." -ForegroundColor DarkGray
    Start-Sleep -Seconds 15
    "https://${Domain}/healthz"
}
Write-Host "  URL: $healthUrl" -ForegroundColor DarkGray
try {
  $resp = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 8 -UseBasicParsing
  if ($resp.StatusCode -eq 200) {
    Write-Host "  healthz OK" -ForegroundColor Green
  } else {
    Write-Warning "healthz returned $($resp.StatusCode)"
  }
} catch {
  Write-Warning "healthz failed: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "== Setup complete ==" -ForegroundColor Green
Write-Host ""
# Display IP — prefer CLI-provided $PublicIpv4, fallback на $VpsIp (всегда есть)
$displayIp = if ($PublicIpv4) { $PublicIpv4 } else { $VpsIp }
$httpsMode = -not [string]::IsNullOrWhiteSpace($Domain)

Write-Host "Endpoints:" -ForegroundColor Cyan
if ($httpsMode) {
  Write-Host "  API:       https://${Domain}"           -ForegroundColor Green
  Write-Host "  WS:        wss://${Domain}/ws"           -ForegroundColor Green
  Write-Host "  Admin:     https://${Domain}/admin/login" -ForegroundColor Yellow
  Write-Host "  TURN:      turn:${displayIp}:3478?transport=udp"
  Write-Host ""
  Write-Host "  (HTTP :${SignalingPort} НЕ exposed наружу — only через Caddy → signaling)" -ForegroundColor DarkGray
  Write-Host "  Клиент ServerApiBaseUrl: https://${Domain}" -ForegroundColor DarkCyan
} else {
  Write-Host "  API:       http://${displayIp}:${SignalingPort}"
  Write-Host "  WS:        ws://${displayIp}:${SignalingPort}/ws"
  Write-Host "  TURN:      turn:${displayIp}:3478?transport=udp"
  if ($AdminPasswordHash) {
    Write-Host "  Admin:     http://${displayIp}:${SignalingPort}/admin/login" -ForegroundColor Yellow
  } else {
    Write-Host "  Admin:     DISABLED (set -AdminPasswordHash to enable)" -ForegroundColor DarkYellow
  }
  Write-Host ""
  Write-Host "  HTTPS НЕ настроен — добавь -Domain connect.example.com -AdminEmail you@example.com" -ForegroundColor DarkYellow
}
Write-Host ""
Write-Host "Логи на VPS:" -ForegroundColor DarkGray
Write-Host "  ssh $remoteHost 'cd $RemoteDir/deploy && docker compose logs -f signaling'"
if ($httpsMode) {
  Write-Host "  ssh $remoteHost 'cd $RemoteDir/deploy && docker compose logs -f caddy'"
}
Write-Host ""
Write-Host "Следующий deploy (10-20 сек):" -ForegroundColor Cyan
if ($httpsMode) {
  Write-Host "  .\deploy\deploy_fast.ps1 -VpsIp $VpsIp -Domain $Domain" -ForegroundColor Yellow
} else {
  Write-Host "  .\deploy\deploy_fast.ps1 -VpsIp $VpsIp" -ForegroundColor Yellow
}
