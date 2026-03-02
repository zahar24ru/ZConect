# Деплой на Ubuntu 24 VPS из Windows (пошагово)

Цель: поднять `signaling + redis + coturn` на Ubuntu 24 LTS и подключить 2 Windows-клиента для проверки видео/клавиатуры/мыши.

## 0) Что понадобится

- VPS с Ubuntu 24 LTS и публичным IPv4.
- Доступ по SSH (логин обычно `root` или пользователь с `sudo`).
- На Windows: PowerShell 5/7 и команда `ssh` (есть в Windows 10/11). Альтернатива: PuTTY.

Порты, которые нужно открыть на VPS (Security Group/Firewall у хостинга + UFW на сервере):

- `8080/tcp` — HTTP API + WebSocket
- `3478/udp` — STUN/TURN
- `49152-49200/udp` — TURN relay (медиа через NAT)

## 0.1) Самый простой способ (автоскрипт из Windows)

Если репозиторий у вас уже лежит на Windows (например, `C:\Soft_pub\ZConect`), можно развернуть всё одной командой:

```powershell
cd C:\Soft_pub\ZConect
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_from_windows.ps1 `
  -VpsIp <VPS_IP> `
  -SshUser root `
  -TurnPass "change_me" `
  -PublicIpv4 <VPS_IP>
```

Скрипт сам:
- упакует `server/ + deploy/ + docs/` в архив
- отправит на VPS по `scp`
- поставит Docker на Ubuntu
- создаст `deploy/.env`
- поднимет `docker compose up -d --build`

Если используете SSH ключ:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\deploy_from_windows.ps1 `
  -VpsIp <VPS_IP> `
  -SshUser ubuntu `
  -SshKeyPath C:\path\to\key.pem `
  -TurnPass "change_me" `
  -PublicIpv4 <VPS_IP>
```

## 1) Подключение к серверу из Windows

В PowerShell:

```powershell
ssh root@<VPS_IP>
```

Если у вас ключ:

```powershell
ssh -i C:\path\to\id_rsa root@<VPS_IP>
```

## 2) Базовая подготовка Ubuntu (на VPS)

После входа по SSH:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git
```

### 2.1 Установка Docker Engine + Compose plugin

```bash
sudo apt-get install -y docker.io docker-compose-plugin
sudo systemctl enable --now docker
docker --version
docker compose version
```

Если команда `docker` требует root, можно запускать через `sudo docker ...`.

## 3) Забрать репозиторий на сервер

Есть 2 варианта — выберите один.

### Вариант A (лучше): репозиторий доступен по Git

```bash
git clone <REPO_GIT_URL> zconect
cd zconect
```

Для обновления:

```bash
cd zconect
git pull
```

### Вариант B: отправить файлы с Windows на VPS (SCP)

На Windows (в папке где лежит проект) можно упаковать архив, отправить и распаковать.

Пример (PowerShell):

```powershell
cd C:\Soft_pub\ZConect
tar -czf zconect.tar.gz .
scp .\zconect.tar.gz root@<VPS_IP>:/root/
```

На VPS:

```bash
mkdir -p /root/zconect
tar -xzf /root/zconect.tar.gz -C /root/zconect
cd /root/zconect
```

## 4) Настроить окружение (`deploy/.env`)

На VPS:

```bash
cd /root/zconect/deploy 2>/dev/null || cd ~/zconect/deploy
```

Создайте/отредактируйте `deploy/.env`:

```bash
cat > .env <<'EOF'
APP_ENV=prod
SIGNALING_PORT=8080
REDIS_ADDR=redis:6379
SESSION_TTL_SEC=300
MAX_JOIN_ATTEMPTS=5
LOCK_MINUTES=10
TURN_REALM=zconect.local
TURN_USER=zconect
TURN_PASS=change_me
PUBLIC_IPV4=<VPS_IP>
EOF
```

Важно:
- `PUBLIC_IPV4` должен быть публичным IPv4 VPS.
- `TURN_PASS` обязательно смените после первого теста.

## 5) Открыть порты в UFW (если UFW включен)

Проверка:

```bash
sudo ufw status
```

Открыть порты:

```bash
sudo ufw allow 22/tcp
sudo ufw allow 8080/tcp
sudo ufw allow 3478/udp
sudo ufw allow 49152:49200/udp
sudo ufw reload
```

## 6) Запуск контейнеров

На VPS, в директории `deploy/`:

```bash
sudo docker compose up -d --build
sudo docker compose ps
```

Проверка health:

```bash
curl -fsS "http://127.0.0.1:8080/healthz" && echo OK
```

Логи:

```bash
sudo docker compose logs -f signaling
sudo docker compose logs -f coturn
```

## 7) Настройка клиента на Windows (оба ноутбука)

В UI клиента:

- `Server API URL`: `http://<VPS_IP>:8080`
- `WebSocket URL`: `ws://<VPS_IP>:8080/ws`
- `STUN URL`: `stun:<VPS_IP>:3478`
- `TURN URL`: `turn:<VPS_IP>:3478?transport=udp`
- `TURN Username`: `zconect`
- `TURN Password`: `change_me` (или ваш)
- `Prefer Relay (TURN)`: включить (для теста через NAT)

Тест сценарий:

1) На ноутбуке A (тот, чей экран будем смотреть): нажать `Создать сессию`.
   - Он работает как host и ждёт viewer.
2) На ноутбуке B: ввести `логин/пароль` и нажать `Подключиться`.
3) На ноутбуке B автоматически откроется отдельное окно `Удалённый экран`.
   - Кликните в окно, чтобы оно получило фокус.
4) Дальше проверить:
   - видео поток
   - мышь (клики/drag)
   - клавиатура (набор текста)

### 7.1) Проверка смены качества/монитора со стороны viewer

1) Подключитесь к host и дождитесь стабильной картинки в окне `Удалённый экран`.
2) В верхней панели окна viewer выберите:
   - `QualityPreset` (`Auto/Low/Medium/High`)
   - `DisplayMode` (`Current/Any/All`)
   - при необходимости заполните `DisplayId`.
3) Нажмите `Применить`:
   - host применит новые параметры захвата “на лету”;
   - viewer получит обновлённый поток и новый `screen_meta`.
4) Если переключение прошло нестабильно (фриз/черный кадр), нажмите `Быстрый реконнект`:
   - host запустит быструю renegotiation (`offer/answer`) без полного переподключения сессии.
5) Проверьте, что после переключения:
   - виден нужный монитор/режим;
   - управление мышью/клавиатурой осталось корректным.

## 8) Типовые ошибки и что прислать мне

Когда что-то не работает — пришлите:

- IP VPS (можно замазать последние цифры)
- содержимое `deploy/.env` без пароля (пароль замажьте)
- вывод команд:
  - `sudo docker compose ps`
  - `sudo docker compose logs --tail 200 signaling`
  - `sudo docker compose logs --tail 200 coturn`
  - `curl -v http://127.0.0.1:8080/healthz`

### Ошибка: `Permission denied (publickey)`

- Неверный SSH ключ/логин. Пришлите команду, которой подключаетесь (`ssh ...`) и сообщение ошибки.

### Ошибка: порт `8080` занят

```bash
sudo ss -lntp | grep :8080 || true
```

Можно поменять порт в `deploy/.env` (`SIGNALING_PORT=...`) и в `docker-compose.yml` (ports).

### Видео есть, но не работает управление / не проходит NAT

- Часто это закрытые UDP порты у провайдера/VPS. Проверьте, что открыты `3478/udp` и диапазон `49152-49200/udp` и что в панели хостинга тоже разрешено UDP.

