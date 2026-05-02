# 🚀 ZConnect Server Deploy Guide — совсем с нуля

Пошаговое руководство как развернуть ZConnect signaling server на голом Ubuntu VPS. Объясняется каждая команда.

## 🔀 Два варианта развёртывания

**ZConnect open-source** — ты сам решаешь, нужен ли HTTPS. Оба варианта полностью функциональны.

### Вариант A: HTTP-mode (быстрый старт, LAN/dev/тесты)

**Нужно:** только VPS с публичным IP.

**Стоимость:** ~300₽/мес (только VPS).

**URL клиента:** `http://<VPS_IP>:8080`

**Когда использовать:**
- Домашняя сеть / локальная установка
- Разработка и тестирование
- Не нужна защита трафика (ты контролируешь обе стороны)

**⚠️ НЕ использовать для публичного деплоя** — traffic без шифрования, MitM attacker может перехватить login_code/pass_code.

### Вариант B: HTTPS-mode (production, публичный деплой)

**Нужно:** VPS + домен + DNS A-record.

**Стоимость:** ~300₽/мес (VPS) + ~200₽/год (домен в reg.ru).

**URL клиента:** `https://connect.example.com`

**Когда использовать:**
- Публичный сервер для множества пользователей
- Защита от MitM в незащищённых сетях (Wi-Fi кафе, мобильные операторы)
- Браузерные клиенты (требуют HTTPS для WebRTC getUserMedia)

**Автоматизация:** Caddy + Let's Encrypt делают всё сами — cert выдаётся при первом запуске, auto-renew каждые 60 дней.

### Что получится в итоге

**HTTP-mode (A):**
- ZConnect signaling server на `http://<VPS_IP>:8080`
- Coturn TURN server на `<VPS_IP>:3478`
- Admin panel в `http://<VPS_IP>:8080/admin/login`

**HTTPS-mode (B):**
- ZConnect signaling server на `https://<твой-домен>`
- WebSocket на `wss://<твой-домен>/ws`
- Admin panel в `https://<твой-домен>/admin/login`
- Coturn TURN server на `<VPS_IP>:3478` (TURN протокол — не TLS, без домена)

**Общее:**
- Telemetry с real-time статистикой подключений
- Auto-update endpoint для client'ов

### Как мигрировать HTTP → HTTPS потом

**Не надо пересоздавать сервер.** Просто купи домен, добавь DNS-запись, и запусти `deploy_setup.ps1` ещё раз с `-Domain` + `-AdminEmail`:

```powershell
# .env smart-merge сохранит TurnPass, AdminHash etc. Не нужно передавать заново:
.\deploy\deploy_setup.ps1 -VpsIp YOUR_SERVER_IP `
  -Domain connect.example.com -AdminEmail you@example.com
```

Signaling-data volume (telemetry.db, audit log) переживает.

---

## 📋 Что установить на свой Windows PC (один раз)

### 1. Go 1.25+ — компилятор для signaling server

**Через Chocolatey** (easier):
```powershell
# Запусти PowerShell ОТ АДМИНИСТРАТОРА (ПКМ → Run as administrator)
choco install golang -y

# Проверь:
go version
# Должно показать: go version go1.26.x windows/amd64
```

**Или скачай с go.dev/dl/** и установи MSI.

**После установки:** закрой все PowerShell/cmd окна, открой новое — PATH обновится.

### 2. OpenSSH client — для ssh/scp

Обычно уже установлен в Windows 10/11. Проверь:
```powershell
ssh -V
# Должно показать: OpenSSH_for_Windows_X.X
scp -V
```

Если нет:
```powershell
# От администратора:
Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
```

### 3. Git — для clone репозитория

```powershell
choco install git -y
```

### 4. (Опционально) VS Code для правки конфигов

```powershell
choco install vscode -y
```

---

## 🖥 Что купить для сервера

- **VPS** с Ubuntu 22.04 или 24.04 LTS, **публичный IPv4**
- **RAM**: 1 GB хватит для 100 concurrent подключений
- **CPU**: 1 vCPU
- **Disk**: 10 GB (telemetry.db растёт ~10 MB / месяц на 1000 users)
- **Пропускная способность**: unlimited желательно — coturn relay использует трафик
- **Открытые порты** (настроить в панели VPS провайдера ДО начала):
  - `22/tcp` (SSH)
  - `8080/tcp` (signaling HTTP — для начального HTTP-mode setup'а; после HTTPS setup будет работать через 443, а 8080 можно закрыть)
  - `80/tcp` (Caddy ACME HTTP-01 challenge — для Let's Encrypt, нужен при HTTPS setup)
  - `443/tcp` + `443/udp` (HTTPS + HTTP/3 QUIC после HTTPS setup)
  - `3478/udp` (TURN)
  - `49152-49200/udp` (TURN relay ports)
- **Root SSH доступ** (логин/пароль или SSH key)

Дешёвые варианты: selectel.ru, reg.ru, aeza.net, serveroid.com — у всех есть Ubuntu базовые образы.

---

## 🎯 Шаг 1 — Подготовить код на PC

### 1.1. Clone репозиторий (или pull если уже есть)

```powershell
# Первый раз:
cd C:\
git clone <ваш-git-repo-url> ZConect
cd ZConect

# Или если уже clone'нуто:
cd C:\Soft_pub\ZConect\.claude\worktrees\upbeat-almeida
git pull
```

### 1.2. Сгенерировать пароль админа

Admin panel (http://server/admin) защищён bcrypt паролем. Делаем хэш:

```powershell
cd server
go build -o admin-hashpass.exe ./cmd/admin-hashpass
.\admin-hashpass.exe
# Admin password (min 8 chars): ТВОЙ_ПАРОЛЬ_ТУТ
```

В stdout появится hash вида:
```
$2a$12$AbCdEfGhIjKlMnOpQrStU.VwXyZ1234567890AbCdEfGhIjKlMnOpQrS
```

**СОХРАНИ ЭТОТ HASH** — он нужен для `-AdminPasswordHash` параметра. Пароль потом не вспомнишь, hash односторонний — помни ТВОЙ ПАРОЛЬ (не hash!).

### 1.3. Проверка что всё компилится

```powershell
# Из C:\Soft_pub\ZConect\.claude\worktrees\upbeat-almeida
cd server

# Кросс-компил для Linux (проверка что код ок):
$env:GOOS='linux'; $env:GOARCH='amd64'; $env:CGO_ENABLED='0'
go build -o signaling-test ./cmd/signaling
# Если успешно — signaling-test создался, можно удалить:
rm signaling-test
$env:GOOS=''; $env:GOARCH=''; $env:CGO_ENABLED=''
cd ..
```

---

## 🎯 Шаг 2 — Первичный deploy на VPS

Это **ОДИН РАЗ**. Потом только `deploy_fast.ps1` (быстрый update за 20 сек).

### 2.1. Запусти setup

```powershell
# Из C:\Soft_pub\ZConect\.claude\worktrees\upbeat-almeida

powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP `
  -SshUser root `
  -TurnPass 'СГЕНЕРИРУЙ_ДЛИННЫЙ_ПАРОЛЬ' `
  -PublicIpv4 YOUR_SERVER_IP `
  -AdminPasswordHash '$2a$12$ТВОЙ_HASH_ИЗ_ШАГА_1.2'
```

Замени:
- `YOUR_SERVER_IP` — **публичный IP твоего VPS**
- `'СГЕНЕРИРУЙ_ДЛИННЫЙ_ПАРОЛЬ'` — **пароль для TURN сервера** (20+ символов, только ASCII, БЕЗ `$` — иначе bash проблемы). Клиентам он не нужен — используется только сервером coturn.
- `'$2a$12$...'` — hash из шага 1.2. **Одинарные кавычки обязательны** — внутри них PowerShell не интерпретирует `$`.

Пример реальной команды:
```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP `
  -TurnPass 'X7zNp4KkQ9wTfM3bSv2YuEd6' `
  -PublicIpv4 YOUR_SERVER_IP `
  -AdminPasswordHash '$2a$12$...'
```

### 2.2. Что происходит во время setup

SSH-пароль спросится 3-5 раз (каждый раз: scp compose, scp binary, несколько ssh команд). В идеале — настроить SSH key для беспарольного доступа (см. ниже).

Скрипт делает:
1. **[1/5] Cross-compile** signaling Go binary на PC для Linux amd64 (~10 сек)
2. **[2/5] Remote setup**:
   - Устанавливает Docker CE на VPS (если ещё нет)
   - Создаёт `/opt/zconect/deploy/bin/` директорию
   - Пишет `/opt/zconect/deploy/.env` с твоими параметрами
   - Открывает UFW ports (если ufw установлен)
3. **[3/5] Upload** — scp docker-compose.fast.yml + binary на VPS
4. **[4/5] Start containers** — `docker compose up -d`, тянет `alpine:3.21` + `coturn/coturn:4.6.2`
5. **[5/5] Healthcheck** — GET `http://VPS:8080/healthz` должен вернуть `ok`

Успешный вывод:
```
== Setup complete ==

Endpoints:
  API:       http://YOUR_SERVER_IP:8080
  WS:        ws://YOUR_SERVER_IP:8080/ws
  TURN:      turn:YOUR_SERVER_IP:3478?transport=udp
  Admin:     http://YOUR_SERVER_IP:8080/admin/login
```

### 2.3. Проверка что работает

Открой в браузере:
- `http://<VPS_IP>:8080/healthz` → должно показать `ok`
- `http://<VPS_IP>:8080/admin/login` → форма входа
- Введи **свой пароль** (тот что вводил в `admin-hashpass.exe`, **не hash**!) → попадёшь в dashboard

---

## 🔁 Шаг 3 — Как обновлять сервер

После изменений в Go коде — **один скрипт, 20 секунд**:

```powershell
cd C:\Soft_pub\ZConect\.claude\worktrees\upbeat-almeida

powershell -ExecutionPolicy Bypass -File .\deploy\deploy_fast.ps1 `
  -VpsIp YOUR_SERVER_IP
```

Что произойдёт:
1. Cross-compile binary на PC (10 сек, кеш используется если код не менялся)
2. scp binary на VPS как `bin/signaling.new`
3. SSH: `docker compose stop signaling` → `mv bin/signaling.new bin/signaling` → `docker compose up -d signaling`
4. Healthcheck

**DB, `.env`, admin hash, audit log — ВСЕ сохраняются.** Fast deploy меняет только binary.

### Только перезапустить контейнер (без пересборки)

Например сбросить in-memory rate limit'ы:
```powershell
.\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -RestartOnly
```

### Сменить admin password

```powershell
cd server
.\admin-hashpass.exe
# Ввести новый пароль → новый hash

cd ..
.\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -RestartOnly `
  -UpdateAdminPasswordHash '$2a$12$НОВЫЙ_HASH'
```

### Посмотреть логи на сервере

```powershell
ssh root@YOUR_SERVER_IP "cd /opt/zconect/deploy && docker compose logs -f signaling"
```

`-f` — tail follow, Ctrl+C чтобы выйти.

Только последние 100 строк:
```powershell
ssh root@YOUR_SERVER_IP "cd /opt/zconect/deploy && docker compose logs --tail=100 signaling"
```

---

## 🔐 SSH без пароля (рекомендую)

Каждый deploy спрашивает SSH password 3-5 раз — затычка. Настрой key-based auth:

### 4.1. Сгенери ключ на PC (если ещё нет)

```powershell
ssh-keygen -t ed25519 -C "zconect-deploy"
# Enter file: [Enter] (по умолчанию ~/.ssh/id_ed25519)
# Enter passphrase: [Enter] (без пароля для automation)
```

### 4.2. Скопируй на VPS

```powershell
# Скопируй содержимое .pub файла:
cat ~/.ssh/id_ed25519.pub

# Подключись по ssh и добавь в authorized_keys:
ssh root@YOUR_SERVER_IP
# На VPS:
mkdir -p ~/.ssh
echo "СОДЕРЖИМОЕ_ID_ED25519.PUB_СЮДА" >> ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys
exit
```

### 4.3. Теперь deploy без паролей

`deploy_fast.ps1` auto-использует `~/.ssh/id_ed25519`. Или передай явно:
```powershell
.\deploy\deploy_fast.ps1 -VpsIp YOUR_SERVER_IP -SshKeyPath C:\Users\zahar\.ssh\id_ed25519
```

---

## 🛟 Troubleshooting

### `healthz failed after 10 attempts`

Container не запустился. Смотри логи:
```powershell
ssh root@<VPS> "cd /opt/zconect/deploy && docker compose logs --tail=50 signaling"
```

Частые причины:
- **Permission denied** на `/app/signaling` — `chmod +x` не применился. Fix:
  ```powershell
  ssh root@<VPS> "chmod +x /opt/zconect/deploy/bin/signaling && cd /opt/zconect/deploy && docker compose restart signaling"
  ```
- **Missing env** — `ADMIN_PASSWORD_HASH` пустой → admin endpoint возвращает 404 (это не error, просто admin выключен).

### Admin login: "Неверный пароль"

- Ты вводишь **пароль**, не hash. Hash односторонний, пароль нельзя расшифровать.
- Забыл пароль? Сгенери новый `admin-hashpass.exe` + deploy с `-UpdateAdminPasswordHash`.

### Admin login: "Слишком много попыток"

Rate limit: 5 попыток per IP per 15 min. Reset:
```powershell
.\deploy\deploy_fast.ps1 -VpsIp <VPS> -RestartOnly
```

### `docker compose` команда unknown

Docker CE старый? Переустанови:
```bash
ssh root@<VPS>
apt-get remove -y docker.io docker-compose
# Тот же deploy_setup.ps1 переустановит новый docker-ce с compose v2
```

### scp ошибка `dest open Failure`

Binary залочен running контейнером. Скрипт должен stop'ать signaling перед mv, но если deploy пришлось прервать — вручную:
```bash
ssh root@<VPS>
cd /opt/zconect/deploy
docker compose stop signaling
ls bin/
# Убедись что bin/signaling.new есть
mv bin/signaling.new bin/signaling
chmod +x bin/signaling
docker compose up -d signaling
```

### Полный reset (удалить всё)

```bash
ssh root@<VPS>
cd /opt/zconect/deploy
docker compose down -v    # -v также удаляет volumes (потеря telemetry.db!)
rm -rf /opt/zconect
```

Потом `deploy_setup.ps1` заново.

---

## 📁 Что где лежит на сервере

```
/opt/zconect/deploy/
├── docker-compose.yml          (наш fast-версия)
├── docker-compose.yml.backup-* (старые versions)
├── .env                         (TURN_PASS, ADMIN_PASSWORD_HASH, etc.)
├── .env.backup-*                (каждый smart merge делает backup)
└── bin/
    ├── signaling                (текущий binary)
    └── signaling.new            (временный во время deploy)

Docker volume 'signaling-data' (persistent):
├── telemetry.db                 (SQLite — heartbeats, device stats)
├── client-version.json          (admin редактирует через /admin UI)
└── admin-audit.log              (NDJSON — все admin actions)
```

Посмотреть volume content:
```bash
ssh root@<VPS> "docker exec zconect-signaling ls -la /app/data/"
```

Подключиться к контейнеру:
```bash
ssh root@<VPS> "docker exec -it zconect-signaling sh"
# Внутри:
cat /app/logs.log         # Server logs
cat /app/data/admin-audit.log  # Admin actions
sqlite3 /app/data/telemetry.db  # НЕ установлен в alpine; нужно apt-get install
exit
```

---

## 🔄 Как устроен deploy под капотом

### 3 скрипта

| Скрипт | Когда | Длительность |
|---|---|---|
| `deploy_setup.ps1` | ОДИН РАЗ (первый deploy или миграция) | 2-5 мин |
| `deploy_fast.ps1` | Каждое обновление | 10-20 сек |
| `deploy_from_windows.ps1` | Fallback когда меняется Dockerfile | 3-10 мин |

### Архитектура fast deploy

```
PC (Windows)                          VPS (Ubuntu)
─────────────────                     ──────────────────
1. go build (linux/amd64)             
   → deploy/bin/signaling             
   (~20 sec, ~10 MB)                  

2. scp signaling →                   /opt/zconect/deploy/bin/signaling.new

3. ssh commands →                    docker compose stop signaling
                                      (releases binary mmap)
                                      mv signaling.new signaling
                                      docker compose up -d signaling

4. curl /healthz ←                   alpine:3.21 container + binary mount
```

**Почему быстро:**
- Нет `docker build` внутри VPS (3-5 мин) — Go компилится на PC (10 сек)
- Нет save/load docker image (100 MB) — только binary (10 MB)
- Volume mount вместо rebuild — контейнер pull'ится раз

**Почему безопасно:**
- Binary mount как `:ro` — контейнер не может изменить файл
- Atomic `mv` — нет момента полу-перезаписанного файла
- Docker volume сохраняет DB между deploy'ями

---

## 🎛 Admin panel — что можно делать

URL: `http://<VPS>:8080/admin/login`

4 вкладки:

### 1. Обзор
- Active sessions (сколько host'ов/viewer'ов подключены прямо сейчас)
- Active peers
- Active rooms (pairs)
- Admin sessions (сколько человек залогинены в admin panel)
- Uptime сервера

### 2. Версия клиента
Форма для push'а нотификации «доступна новая версия»:
- **latest_version**: `1.0.1`
- **download_url**: `https://yourdomain.com/download/ZConect-Setup-1.0.1.exe`
- **release_notes**: что нового (markdown в будущем)
- **min_compatible_version**: минимальная версия с которой можно жить

Клиенты polling'ят `/api/v1/client/version` каждые 6 часов (+ раз на startup). Если `latest_version > current` — видят amber badge в шапке приложения. Клик → открывает `download_url` в браузере. Auto-install НЕТ.

### 3. Server Health
Runtime статистика:
- Uptime
- Go goroutines count
- Heap + Sys memory
- Active sessions/peers/rooms
- Admin sessions active

Обновляется каждые 10с.

### 4. Телеметрия
Статистика по клиентам из heartbeat'ов:
- Online (кто был в последние 10 мин)
- Total installed
- Distribution: OS / Language / Country / App version
- Android device models (если есть)
- Recent devices (20 шт): IP, страна, версия, last seen

Фильтр по Platform: All / Windows / Android.

---

## 📊 Что собирает телеметрия

Клиент при каждом старте и потом раз в час отправляет `POST /api/v1/telemetry/heartbeat`:

```json
{
  "machine_id": "43c4d5def6764eedbd1da8af90a40105",
  "app_version": "1.0.0",
  "os_version": "10.0.22631",
  "os_language": "ru-RU",
  "platform": "windows",
  "device_model": ""
}
```

Server сохраняет в SQLite `telemetry.db`:
- machine_id (uuid клиента)
- ip (исходный + geoip country)
- timestamp last_seen
- platform, os, version, language

Никаких personal data. Никаких connection logs. Только факт что машина X активна.

---

## 🔒 HTTPS (Caddy + Let's Encrypt) — production setup

После HTTP setup работает, добавляем TLS. Бесплатно, автоматически, renew раз в 60 дней.

### Что нужно
1. **Домен** (например `zconect.ru` в reg.ru ~200₽/год, или `.com` в Namecheap ~10$/год)
2. **DNS A-record `connect → VPS_IP`** — заходишь в control panel регистратора, добавляешь запись `connect` (subdomain) → IP твоего VPS. Подождать 5-60 мин пока propagation пройдёт.
3. **Email** для Let's Encrypt expiry notifications (LE требует)

### Проверка DNS (перед setup'ом)
```powershell
# Windows
nslookup connect.yourdomain.ru

# Должен показать твой VPS IP. Если нет — DNS ещё не propagated, подожди.
```

### Setup с HTTPS
```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP -SshUser root `
  -TurnPass "StrongPass" -PublicIpv4 YOUR_SERVER_IP `
  -AdminPasswordHash '$2a$12$...' `
  -Domain connect.yourdomain.ru `
  -AdminEmail you@example.com
```

### Что произойдёт автоматически
- Caddy получает TLS cert через ACME HTTP-01 challenge (порт 80)
- `ADMIN_COOKIE_SECURE=true` — cookie станет HTTPS-only
- UFW открывает 80, 443/tcp, 443/udp (HTTP/3)
- Signaling container больше не exposes 8080 наружу — только через Caddy
- Healthcheck stucking в `https://connect.yourdomain.ru/healthz`
- Первый раз занимает ~15 сек (ACME handshake)

### Endpoint после setup'а
- API: `https://connect.yourdomain.ru`
- WebSocket: `wss://connect.yourdomain.ru/ws`
- Admin: `https://connect.yourdomain.ru/admin/login`

### Client config
- **WPF**: в Settings → Server API URL ввести `https://connect.yourdomain.ru` — WebSocket URL автоматически станет `wss://...`
- **Android**: в Settings → Server Address ввести `https://connect.yourdomain.ru` — httpBase/wsBase авто-detect scheme

### Сертификат persist (ВАЖНО)
Caddy хранит certs в `caddy-data` Docker volume. **Не удаляй этот volume** — Let's Encrypt имеет rate limit 50 certs/week/domain. Если пересоздавать — упрёмся за день.

```bash
# Проверить cert:
ssh root@VPS "docker exec zconect-caddy ls /data/caddy/certificates/acme-v02.api.letsencrypt.org-directory/"

# Логи Caddy (ACME challenges, renewal):
ssh root@VPS "cd /opt/zconect/deploy && docker compose logs -f caddy"
```

### Troubleshooting HTTPS
- **DNS не propagated** — `nslookup` не показывает нужный IP → подожди 5-60 мин
- **Port 80 закрыт** — ACME HTTP-01 challenge требует 80 порт. `ssh root@VPS "sudo ufw status | grep 80"`
- **Типо в домене** (например `connec` вместо `connect`) — Caddy попытается получить cert для неправильного домена. Исправь DNS и пересоздай volume: `docker compose down && docker volume rm zconect_caddy-data && docker compose up -d`
- **Rate limit Let's Encrypt** — 50 certs/week/domain. Если упёрся, подожди неделю или используй staging CA для тестов (раскомментировать `acme_ca` в Caddyfile.template)

Детали: [`deploy/README.md#https-caddy--lets-encrypt`](../deploy/README.md).

---

## 🚦 Next steps

1. **Купить домен** (см. HTTPS секцию выше)
2. **Настроить HTTPS** (см. выше — всё автоматизировано в deploy_setup.ps1)
3. **Обновить endpoint в client'е** — `settings.ServerApiBaseUrl = https://connect.yourdomain.ru`
4. **Запустить unit-тесты клиента против prod** — `$env:ZCONECT_TEST_API='https://connect.yourdomain.ru' ; $env:ZCONECT_TEST_WS='wss://connect.yourdomain.ru/ws' ; dotnet test`

---

## 📞 Всё сломалось

1. **Проверь healthz**: `curl http://<VPS>:8080/healthz` — если не отвечает, сервер down
2. **Посмотри логи**: `ssh root@<VPS> "cd /opt/zconect/deploy && docker compose logs --tail=100 signaling"`
3. **Проверь `.env`**: `ssh root@<VPS> "cat /opt/zconect/deploy/.env"`
4. **Восстанови из backup**: `ssh root@<VPS> "cp /opt/zconect/deploy/.env.backup-YYYYMMDD-HHMMSS /opt/zconect/deploy/.env && cd /opt/zconect/deploy && docker compose restart signaling"`
5. **Полный re-deploy**: `deploy_setup.ps1 -VpsIp <VPS> -ForceResetEnv` (с параметрами)
