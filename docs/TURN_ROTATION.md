# Rotating TURN credentials (RFC 7635)

Per-client short-term TURN credentials выдаваемые signaling server'ом и HMAC-подписанные для валидации coturn'ом. Implementation следует [draft-uberti-behave-turn-rest-00](https://datatracker.ietf.org/doc/html/draft-uberti-behave-turn-rest-00) — этот же подход используется Twilio, CloudFlare Calls, Daily.co, Jitsi.

## Зачем

**Static TURN username/password** имеет проблемы:
- Если leak'нется (через log, network capture, reverse-engineered client) — attacker может использовать наш coturn как anonymous relay бесконечно, до manual password change
- Ротация требует coturn restart → прерывает активные relay sessions
- Все клиенты используют один пароль — нет audit'а кто именно использовал relay

**Rotating credentials** решают всё:
- ✅ Каждый client получает **уникальные** creds с expiration
- ✅ Leaked creds бесполезны через 30 мин
- ✅ Никакого restart'а coturn — rotation автоматическая (HMAC deterministic)
- ✅ Username содержит session ID — coturn logs показывают кто использовал relay

## Как работает

```
┌─────────────┐                    ┌────────────┐                 ┌──────────┐
│  Client     │                    │ Signaling  │                 │  Coturn  │
└──────┬──────┘                    └─────┬──────┘                 └────┬─────┘
       │                                 │                              │
       │  POST /api/v1/session/create    │                              │
       ├────────────────────────────────→│                              │
       │                                 │ (reads TURN_AUTH_SECRET      │
       │                                 │  from env, generates HMAC    │
       │                                 │  creds for this session)     │
       │                                 │                              │
       │  200 OK                         │                              │
       │  { turn_servers: [{             │                              │
       │    username: "1714123456:S1",   │                              │
       │    credential: "base64_hmac",   │                              │
       │    ttl_seconds: 1800 }]}        │                              │
       │←────────────────────────────────┤                              │
       │                                 │                              │
       │  TURN Allocate Request          │                              │
       │  Authorization: username=...    │                              │
       │  Message-Integrity = HMAC(      │                              │
       │    credential, ... )            │                              │
       ├─────────────────────────────────────────────────────────────────→│
       │                                 │                              │ (parses username,
       │                                 │                              │  checks expiry > now,
       │                                 │                              │  recomputes HMAC
       │                                 │                              │  from its TURN_AUTH_SECRET,
       │                                 │                              │  compares)
       │  200 OK  (relay allocated)      │                              │
       │←─────────────────────────────────────────────────────────────────┤
       │                                 │                              │
```

**Ключевое:** signaling server и coturn share один **shared secret** (`TURN_AUTH_SECRET`). Signaling генерирует HMAC от `username = "<exp_ts>:<session_id>"`, coturn валидирует тем же secret'ом. Один secret, один алгоритм → consistent results.

## Configuration

### Server (.env)

```bash
# Shared secret (НЕ менять часто — все активные clients перестанут auth'иться).
# Generate: openssl rand -hex 32
TURN_AUTH_SECRET=64-character-hex-string

# TTL per-client credential. Default 30 мин, max 24 часа, min 5 мин.
TURN_CRED_TTL_SEC=1800

# Публичный host/IP coturn'а для client ICE config.
# Если пусто — server использует PUBLIC_IPV4.
TURN_PUBLIC_HOST=connect.example.com

# TURN listening port (default 3478 UDP)
TURN_PUBLIC_PORT=3478
```

### Coturn (docker-compose.fast.yml)

```yaml
coturn:
  command: >
    -n
    --use-auth-secret
    --static-auth-secret=${TURN_AUTH_SECRET}
    --realm=${TURN_REALM:-zconect.local}
    --external-ip=${PUBLIC_IPV4}
    --listening-port=3478
    --min-port=49152
    --max-port=49200
    --no-tls
    --no-dtls
```

Важно: `--lt-cred-mech` и `--use-auth-secret` — **взаимоисключающие**. При миграции со старого формата убрать `--lt-cred-mech` + `--user=...`.

## Deployment

```powershell
# Setup генерирует TURN_AUTH_SECRET автоматически если не указан
.\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP `
  -Domain connect.example.com `
  -AdminEmail you@example.com

# Явное указание secret'а (при миграции или восстановлении):
.\deploy\deploy_setup.ps1 `
  -VpsIp YOUR_SERVER_IP `
  -TurnAuthSecret "existing-64-hex-char-secret" `
  -TurnCredTtlSec 1800
```

Secret сохраняется в `.env` на сервере и переживает deploy'и (smart-merge).

## Client behavior

### WPF (C#)

Sessions API client парсит `turn_servers` из response'ов:
- `CreateSessionResponse.TurnServers` (host creates session)
- `JoinSessionResponse.TurnServers` (viewer joins)
- `RefreshSessionResponse.TurnServers` (fresh creds при refresh)

`MainViewModel._dynamicTurnServers` хранит последние creds, `BuildTransportSettings` использует их вместо static config в `ClientSettings.TurnUrl/TurnUsername/TurnPassword`.

### Android (Kotlin)

`SessionApi.JoinResponse.turnServers` — parsed automatically. `RemoteScreen` принимает `initialTurnServers: List<TurnServerConfig>?` и передаёт их в `PeerConnectionManager.initialize(rotatingTurnServers = ...)`.

## Когда создаются новые creds

Signaling server генерирует свежие credentials в **каждом** из этих responses:
- `POST /api/v1/session/create` — новая сессия
- `POST /api/v1/session/join` — viewer заходит
- `POST /api/v1/session/refresh` — host продлевает сессию

Клиент должен вызывать `/session/refresh` до `expires_at_unix` чтобы получить fresh creds. Default TTL 30 мин — достаточно для большинства sessions (обычно короче).

## Два клиента с разными паролями — могут связаться?

**Да, легко.** TURN password проверяет "можешь ли ты ИСПОЛЬЗОВАТЬ coturn как relay", а не "с кем ты хочешь говорить".

Flow:
1. Host auth'ится в coturn с creds A → получает relay slot `coturn:49155`
2. Viewer auth'ится с creds B → получает свой relay slot `coturn:49201`
3. Signaling обменивает их SDP (содержит relay addresses друг друга)
4. ICE connectivity check finds работающую candidate pair
5. WebRTC устанавливает P2P через relay slots

Coturn просто forwards пакеты согласно TURN permissions — ему не важно что два clients связаны между собой.

## Security considerations

- **TURN_AUTH_SECRET leak** — эквивалентно root access к coturn'у. Attacker может генерить свои creds для anonymous relay. Mitigation: хранить secret только в `.env` на сервере, не логировать, не коммитить.
- **Clock skew** — coturn проверяет `now() < exp_ts`. Если server clock drift > TTL — valid creds могут rejected. Mitigation: NTP sync (Ubuntu default).
- **Replay attacks** — credentials работают до `exp_ts`, любой у кого они есть может использовать. RFC 7635 это acknowledges; production systems полагаются на short TTL (наш default 30 мин).
- **Session ID enumeration** — username contains session ID, но видеть это может только attacker с network access к TURN traffic (STUN messages). Session ID сам по себе не выдаёт login_code / pass_code.

## Rollback на static creds

Если нужно откатить к legacy auth (например для debug):

```bash
# На VPS:
ssh root@VPS_IP
cd /opt/zconect/deploy

# 1. Убрать TURN_AUTH_SECRET из .env (или пустую строку)
sed -i 's/^TURN_AUTH_SECRET=.*/TURN_AUTH_SECRET=/' .env

# 2. Добавить static TURN_USER/TURN_PASS
echo "TURN_USER=zconect" >> .env
echo "TURN_PASS=your-static-password" >> .env

# 3. Обратно на --lt-cred-mech в docker-compose.yml
# (временно — нужен rebuild docker-compose.fast.yml с --lt-cred-mech + --user=)

# 4. Restart
docker compose restart signaling coturn
```

Клиенты без поддержки rotating TURN (старые builds) автоматически fall back на static config из своих settings когда `turn_servers` field absent в response.

## Tests

- Unit tests: `server/internal/turn/credentials_test.go` (HMAC generation, verification, expiry checks)
- Integration test TODO: поднять coturn в docker + проверить что signaling-generated creds валидируются

## References

- [RFC 7635 — Session Traversal Utilities for NAT (STUN) Extension for Third-Party Authorization](https://datatracker.ietf.org/doc/html/rfc7635)
- [draft-uberti-behave-turn-rest-00 — REST API for TURN credentials](https://datatracker.ietf.org/doc/html/draft-uberti-behave-turn-rest-00) (coturn's implementation basis)
- [coturn docs — Authentication](https://github.com/coturn/coturn/wiki/turnserver#authentication-options)
- [Twilio NTS — same pattern](https://www.twilio.com/docs/stun-turn/api) (production reference)
