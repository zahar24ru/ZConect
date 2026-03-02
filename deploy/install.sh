#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"

echo "[1/5] Checking docker..."
if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is not installed. Please install Docker Engine first."
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose plugin is missing. Please install docker-compose-plugin."
  exit 1
fi

echo "[2/5] Preparing env..."
if [ ! -f "${DEPLOY_DIR}/.env" ]; then
  cat > "${DEPLOY_DIR}/.env" <<'EOF'
APP_ENV=prod
SIGNALING_PORT=8080
REDIS_ADDR=redis:6379
SESSION_TTL_SEC=300
MAX_JOIN_ATTEMPTS=5
LOCK_MINUTES=10
TURN_REALM=zconect.local
TURN_USER=zconect
TURN_PASS=change_me
PUBLIC_IPV4=127.0.0.1
EOF
fi

echo "[3/5] Starting containers..."
cd "${DEPLOY_DIR}"
docker compose up -d --build

echo "[4/5] Waiting healthcheck..."
for _ in $(seq 1 20); do
  if curl -fsS "http://127.0.0.1:8080/healthz" >/dev/null 2>&1; then
    echo "Healthcheck OK"
    break
  fi
  sleep 1
done

echo "[5/5] Done."
echo "Signaling: http://<server-ip>:8080"
echo "TURN/STUN: <server-ip>:3478/udp"
