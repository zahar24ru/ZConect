# Deploy

Деплой ZConnect signaling server на Ubuntu VPS.

## 🔀 Выбор режима: HTTP или HTTPS?

ZConnect можно развернуть в двух режимах. **Оба полностью функциональны** — выбирай по ситуации.

| Критерий | HTTP-mode (без домена) | HTTPS-mode (Caddy + Let's Encrypt) |
|---|---|---|
| **Что нужно** | Только VPS с IP | VPS + домен + DNS A-record |
| **Стоимость** | Только VPS (~300₽/мес) | VPS + домен (~200₽/год) |
| **URL клиента** | `http://YOUR_SERVER_IP:8080` | `https://connect.example.com` |
| **Шифрование** | ❌ трафик в открытом виде | ✅ TLS 1.2/1.3, auto-renew |
| **Admin cookie secure** | `false` (работает по HTTP) | `true` (HTTPS-only, обязательно) |
| **Production ready** | ❌ MitM возможен, passwords видны в сети | ✅ да |
| **Хорошо для** | LAN, dev, тесты, быстрый старт | Публичное развёртывание с реальными пользователями |
| **Миграция HTTP → HTTPS** | — | Запустить `deploy_setup.ps1` ещё раз с `-Domain` — `.env` сохранится (smart merge) |

### HTTP-mode — минимальный setup (без Caddy вообще)

Если хочешь совсем минимал (только signaling + coturn без Caddy-placeholder'а):

```powershell
# Setup как обычно, БЕЗ -Domain / -AdminEmail:
.\deploy\deploy_setup.ps1 -VpsIp YOUR_SERVER_IP `
  -TurnPass "..." -PublicIpv4 YOUR_SERVER_IP `
  -AdminPasswordHash '$2a$12$...'
```

В HTTP-mode Caddy всё равно стартует как placeholder (~5 MB RAM, на порту 80 просто возвращает статический текст). Если хочешь полностью отключить Caddy:

```bash
# На VPS, после setup'а:
ssh root@VPS_IP
cd /opt/zconect/deploy
docker compose stop caddy
docker compose rm -f caddy

# Чтобы Caddy не стартовал при compose up — закомментировать caddy: секцию
# в docker-compose.yml, либо использовать compose profile:
docker compose up -d signaling coturn   # только нужные сервисы
```

При этом UFW должен открывать `8080/tcp` (см. `deploy_setup.ps1` — автоматически открывает в HTTP-mode).

### HTTPS-mode

См. раздел [«HTTPS (Caddy + Let's Encrypt)»](#https-caddy--lets-encrypt) ниже.

---

## Сравнение workflow'ов

| Скрипт | Длительность | Когда использовать |
|---|---|---|
| **`deploy_fast.ps1`** | **10-20 сек** | Ежедневный deploy — обновление Go кода |
| `deploy_setup.ps1` | 2-5 мин | Первоначальная установка (один раз) |
| `deploy_from_windows.ps1` | 3-10 мин | Full rebuild — при изменениях Dockerfile / go.mod major / системных deps |

## Миграция с существующего deploy'а

**Не требует полного сброса сервера.** `deploy_setup.ps1` сохраняет:
- ✅ Docker volume `signaling-data` (telemetry.db) — имя volume не меняется
- ✅ Существующий `.env` — **smart merge** (CLI params переопределяют, остальное сохраняется)
- ✅ Старый `docker-compose.yml` → backup в `.backup-YYYYMMDD-HHMMSS`
- ✅ Старый `.env` → backup в `.env.backup-YYYYMMDD-HHMMSS`

Что заменяется:
- 🔄 `docker-compose.yml` — на fast-версию (alpine + binary mount)
- 🔄 Signaling container — пересоздаётся (image меняется с `zconect-signaling:latest` на `alpine:3.21`)
- 🔄 coturn container — пересоздаётся (с теми же env параметрами)

### Migration flow
```powershell
# При миграции TurnPass/PublicIpv4 можно НЕ передавать — они возьмутся из старого .env:
.\deploy\deploy_setup.ps1 -VpsIp YOUR_SERVER_IP

# Или с явными параметрами (CLI > .env):
.\deploy\deploy_setup.ps1 -VpsIp YOUR_SERVER_IP -TurnPass "NewPass" -AdminPasswordHash '$2a$12$...'

# Полная перезапись .env (если нужно — бэкап всё равно сохраняется):
.\deploy\deploy_setup.ps1 -VpsIp YOUR_SERVER_IP -TurnPass "..." -PublicIpv4 "..." -ForceResetEnv
```

## Fast deploy workflow

### Первоначальная установка (fresh VPS)

```powershell
# 1. Сгенерировать bcrypt hash для admin panel (опционально):
cd server
go build -o admin-hashpass.exe ./cmd/admin-hashpass
./admin-hashpass.exe    # вводит пароль, выводит hash

# 2. Setup VPS — устанавливает Docker, создаёт директории, пишет .env, поднимает контейнеры
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP -SshUser root `
  -TurnPass "StrongPass123" -PublicIpv4 YOUR_SERVER_IP `
  -AdminPasswordHash '$2a$12$...'
```

На fresh VPS `TurnPass` и `PublicIpv4` обязательны. При повторном запуске на VPS с существующим `.env` — любой параметр становится optional (берётся из .env).

### HTTPS (Caddy + Let's Encrypt)

ZConnect поддерживает автоматический TLS через Caddy reverse proxy. Let's Encrypt выдаёт бесплатные сертификаты на 90 дней, Caddy авто-renew за 30 дней до expiry.

**Prerequisites:**
1. Домен (например `connect.example.com`), DNS A-record `connect → VPS_IP` (уже propagated — проверь `nslookup connect.example.com`)
2. Порты 80 и 443 открыты наружу (deploy_setup.ps1 добавит в UFW автоматически при `-OpenUfwPorts`)
3. Email для Let's Encrypt expiry notifications (LE требует)

**Setup:**
```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP -SshUser root `
  -TurnPass "StrongPass" -PublicIpv4 YOUR_SERVER_IP `
  -AdminPasswordHash '$2a$12$...' `
  -Domain connect.example.com `
  -AdminEmail you@example.com
```

Что произойдёт:
- Caddyfile генерируется из `Caddyfile.template` с substitution `{Domain, AdminEmail}` и загружается на сервер
- `SIGNALING_DOMAIN` + `ADMIN_EMAIL` пишутся в `.env`
- `ADMIN_COOKIE_SECURE=true` force-set (обязательно при HTTPS)
- UFW открывает 80/tcp, 443/tcp, 443/udp (HTTP/3)
- Signaling container НЕ exposes порт наружу — только через docker network к Caddy
- Caddy при первом request на `https://connect.example.com` автоматически запрашивает cert (ACME HTTP-01 challenge через 80 порт)
- Первый healthcheck ждёт 15 сек на Let's Encrypt handshake

**Endpoints после HTTPS setup:**
- `https://connect.example.com`         — signaling API
- `wss://connect.example.com/ws`        — WebSocket
- `https://connect.example.com/admin/login` — admin panel
- `turn:VPS_IP:3478?transport=udp`      — TURN (без TLS, на TURN порту 3478)

**Cert storage — persist:**
- `caddy-data` volume содержит certs + private keys + OCSP staples
- **ОЧЕНЬ ВАЖНО**: Let's Encrypt rate limit 50 certs/week/domain. Если пересоздавать volume на каждом deploy — упрёмся в rate limit за день. Volume НЕ трогается при `deploy_fast.ps1`.

**Fast deploy с HTTPS:**
```powershell
# Domain передаётся только для healthcheck URL, Caddy config уже на сервере:
.\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -Domain connect.example.com
```

**Client config:**
- WPF: `ServerApiBaseUrl = https://connect.example.com` — WebSocket URL авто-становится `wss://` (см. `ResolveWsUrl` в `MainViewModel.Connection.cs`)
- Android: в Settings → Server Address ввести `https://connect.example.com` — `httpBaseUrl`/`wsBaseUrl` в `AppSettings.kt` auto-detect scheme

**Rollback на HTTP:**
Оставить `-Domain` пустым (или не передавать) при следующем `deploy_setup.ps1`. Caddyfile перепишется в HTTP-only mode. После rollback НЕ удаляй `caddy-data` volume — certs остаются валидными до next setup.

**Troubleshooting HTTPS:**
```bash
# Caddy логи — ACME challenge errors, cert renewal status
ssh root@VPS_IP 'cd /opt/zconect/deploy && docker compose logs --tail=100 caddy'

# Проверить что Caddy поднялся
ssh root@VPS_IP 'docker ps | grep caddy'

# Manual cert status
ssh root@VPS_IP 'docker exec zconect-caddy caddy list-modules | head -5'
```

Частые ошибки:
- **DNS ещё не propagated** — `nslookup connect.example.com` не показывает правильный IP → подожди 5-60 мин
- **Port 80 закрыт** — ACME HTTP-01 challenge требует 80 порт. Убедись `sudo ufw status | grep 80`
- **Типо в домене** — Caddy будет пытаться получить cert для `connec.example.com` и failing. Исправь DNS-запись и пересоздай volume: `docker compose down && docker volume rm zconect_caddy-data && docker compose up -d`

### Каждый последующий deploy (10-20 сек)

```powershell
# Обычный случай — обновление Go кода
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP

# Обновить только ADMIN_PASSWORD_HASH (без rebuild кода)
.\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -RestartOnly `
  -UpdateAdminPasswordHash '$2a$12$NewHash'

# Пропустить healthcheck (если VPS не доступен из этой сети)
.\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -SkipHealthcheck
```

## Что сохраняется между deploy'ями

| Файл / Volume | Содержимое | Persist |
|---|---|---|
| `signaling-data` Docker volume | `telemetry.db`, `client-version.json`, `admin-audit.log` | ✅ Да, переживает restart/redeploy |
| `/opt/zconect/deploy/.env` | TURN_PASS, ADMIN_PASSWORD_HASH и т.д. | ✅ Только ручное обновление через `-UpdateAdminPasswordHash` или ssh |
| `/opt/zconect/deploy/bin/signaling` | Go binary | ❌ Заменяется каждый fast deploy |
| `/opt/zconect/deploy/docker-compose.yml` | Compose config | ❌ Перезаливается из `docker-compose.fast.yml` при setup |

## Архитектура fast deploy

```
PC (Windows)                              VPS (Ubuntu)
─────────────────                         ────────────────
                                          
1. go build (linux/amd64, CGO=0)          
   → deploy/bin/signaling (~20 MB)        
                                          
2. scp signaling                          → /opt/zconect/deploy/bin/signaling.new
                                          
3. ssh                                    → mv signaling.new signaling
                                            chmod +x signaling
                                            docker compose restart signaling
                                            
4. HTTP GET /healthz                      ← alpine:3.21 + mount binary
```

**Почему быстро:**
- Нет `docker build` (3-5 мин) — только `go build` на PC (5-10 сек)
- Нет `docker save | gzip` (100 MB) — только binary (~20 MB)
- `docker compose restart` вместо `up` — image не пересоздаётся

**Почему безопасно:**
- Binary mount как `:ro` — контейнер не может изменить файл
- `signaling.new` → `mv` = атомарная замена (нет момента полу-перезаписанного файла)
- `signaling-data` volume — DB и audit log НЕ трогаются при redeploy

## Admin panel

После setup с `-AdminPasswordHash`:
```
http://VPS_IP:8080/admin/login
```

**10 tabs** (см. [`docs/API.md#admin-panel-endpoints`](../docs/API.md#admin-panel-endpoints) для полного списка endpoints):
- **Обзор** — health stats + maintenance mode toggle + DB backup download + reset rate limit + logout others
- **Версия клиента** — edit `client-version.json` для auto-update notification badge
- **Server Health** — runtime (uptime, goroutines, memory)
- **Телеметрия** — aggregated stats from client heartbeats
- **WebRTC сессии** — live list активных sessions с brute-force status (TotalFailedAttempts, Tier 0-5, Lock countdown) + force Kill action
- **Аудит** — tail NDJSON audit log (login/ban/password-change/etc)
- **Логи** — tail server logs (filter WARN/INFO/DEBUG)
- **Admin сессии** — active admin sessions list + revoke
- **IP блокировки** — manual IP ban list (permanent)
- **Подозрительные IP** (2026-04-22) — per-IP enumeration counter за 5-мин sliding window. Detector для brute-force attacker'ов которые перебирают login_codes. Кнопка "Забанить" pre-fills IP в ban form. Automatic ban **НЕ делается** — NAT users защищены от false positives.
- **Пароль** — change admin password без SSH (persist в `/app/data/admin-password.hash` через volume)

Смена пароля без rebuild:
```powershell
# Генерация нового hash:
cd server && ./admin-hashpass.exe

# Обновление на VPS (persist hash file предпочтительнее env var — пережив restart):
.\deploy\deploy_fast.ps1 -VpsIp X.X.X.X -RestartOnly -UpdateAdminPasswordHash '$2a$12$...'

# Или через admin UI: tab "Пароль" — полностью без SSH/deploy.
```

IP whitelist — отредактировать `ADMIN_ALLOWED_IPS` в `.env` напрямую через ssh:
```bash
ssh root@VPS_IP
vi /opt/zconect/deploy/.env
# ADMIN_ALLOWED_IPS=1.2.3.4,5.6.7.8
cd /opt/zconect/deploy && docker compose restart signaling
```

## Session brute-force protection

Per-session progressive lockout (`server/internal/session/service.go`):
- 5 wrong pass → tier 1 lock 15 сек
- 5 more → tier 2 lock 2 минуты
- 5 more → tier 3 lock 15 минут
- 5 more → tier 4 lock 1 час
- 5 more → **StateBlocked permanent** (423 Locked response)

Host разблокирует через `POST /session/refresh?regenerate_pass=true` (manual
"Refresh password" button в UI или auto-refresh timer).

`TotalFailedAttempts` cumulative counter виден в admin UI вкладке
«WebRTC сессии» — NE resets на tier escalation, ресет только на success join
или password refresh.

Детали: [`docs/API.md §3.1`](../docs/API.md#31-session-brute-force-protection-per-session-not-per-ip).

## Логи

```bash
ssh root@VPS_IP 'cd /opt/zconect/deploy && docker compose logs -f signaling'
ssh root@VPS_IP 'cd /opt/zconect/deploy && docker compose logs -f coturn'
```

## Troubleshooting

### `healthz failed after 10 attempts`

Binary упал при старте. Проверь:
```bash
ssh root@VPS_IP 'cd /opt/zconect/deploy && docker compose logs --tail=50 signaling'
```

Распространённые причины:
- **Permission denied** — `chmod +x` не применился. На VPS: `sudo chmod +x /opt/zconect/deploy/bin/signaling && docker compose restart signaling`
- **Cross-compile mismatch** — редко. Проверить `file bin/signaling` — должен быть ELF 64-bit LSB executable, x86-64.
- **Missing env** — `ADMIN_PASSWORD_HASH` в `.env` не задан → admin endpoint возвращает 404 (это ок, не ошибка).

### «`docker compose build`» триггерит full rebuild

`docker-compose.fast.yml` **не имеет** `build:` секции — только `image: alpine:3.21`. Если случайно запустили `docker compose up --build` — это не эффект. Используйте `restart` или `up` (без `--build`).

### Full rebuild требуется

Если меняется Dockerfile, go.mod major version, или базовый Docker образ:
```powershell
# Fallback на full rebuild
.\deploy\deploy_from_windows.ps1 -VpsIp ... -TurnPass ... -PublicIpv4 ...
```

## Files

- `docker-compose.fast.yml` — thin alpine + binary mount (default после setup)
- `docker-compose.yml` — full build с Dockerfile (для `deploy_from_windows.ps1`)
- `deploy_setup.ps1` — initial setup (Docker install, directories, .env, первый deploy)
- `deploy_fast.ps1` — ежедневный deploy (compile + scp + restart)
- `deploy_from_windows.ps1` — full rebuild (для major changes)
- `install.sh` — manual install script (без PowerShell)
- `bin/signaling` — cross-compiled Go binary (generated, gitignored)
