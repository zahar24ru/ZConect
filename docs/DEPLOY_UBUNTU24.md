# Развертывание на Ubuntu 24 LTS (VPS)

## 1) Компоненты

- `signaling` (Go сервис)
- `redis` (сессии и rate-limit счетчики)
- `coturn` (STUN/TURN)
- `nginx/caddy` (TLS termination, по желанию)

## 2) Деплой (после клонирования репозитория)

Скрипт `install.sh` нужно запускать **из каталога репозитория** (он ожидает, что рядом есть `server/` и `deploy/`):

```bash
git clone https://github.com/<org>/<repo>.git
cd <repo>
./deploy/install.sh
```

Если вы деплоите на VPS из Windows, см. пошаговую инструкцию:

- `docs/DEPLOY_FROM_WINDOWS.md`

Скрипт `install.sh` делает:

1. Проверяет наличие Docker и Docker Compose plugin.
2. Создает `deploy/.env` из шаблона (если файла ещё нет).
3. Запускает `docker compose up -d --build` (signaling, redis, coturn).
4. Проверяет `healthz`.

**Важно:** для работы TURN из интернета в `deploy/.env` укажите реальный внешний IP сервера в переменной `PUBLIC_IPV4` (не `127.0.0.1`).

Если one-command запуск невозможен (ограниченный shell), fallback:

```bash
cd deploy && docker compose up -d
```

## 3) Переменные окружения

Пример `.env`:

```env
APP_ENV=prod
SIGNALING_PORT=8080
REDIS_ADDR=redis:6379
SESSION_TTL_SEC=300
MAX_JOIN_ATTEMPTS=5
LOCK_MINUTES=10
TURN_REALM=zconect.local
TURN_USER=zconect
TURN_PASS=change_me
PUBLIC_IPV4=1.2.3.4
```

## 4) Автозапуск

- `docker compose up -d`
- `restart: unless-stopped` у всех контейнеров.
- Для обновления: `docker compose pull && docker compose up -d`.
- Для полного автообновления можно добавить `systemd timer` (опционально).

## 5) Надежность

- healthchecks для signaling и redis.
- логирование контейнеров + лимит размера логов.
- резервный snapshot Redis (опционально).

## 6) Безопасность

- Только TLS для API/WS.
- Ограничение частоты запросов на уровне reverse proxy.
- Firewall: только нужные порты.
- Fail2ban (опционально) для грубых атак.
