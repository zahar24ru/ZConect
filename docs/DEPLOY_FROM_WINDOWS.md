# Деплой ZConnect на VPS с Windows

Всё делается с вашего Windows ПК. На сервере ничего не компилируется и не скачивается из интернета.

---

## Что нужно подготовить

**На вашем ПК:**
- Docker Desktop — запущен
- PowerShell 5 или 7
- SSH-клиент (встроен в Windows 10/11)

**VPS:**
- Ubuntu 24 LTS
- SSH-доступ (root или sudo-пользователь)
- Открытые порты в панели хостинга:

| Порт | Протокол | Для чего |
|------|----------|----------|
| 8080 | TCP | API + WebSocket |
| 3478 | UDP | STUN/TURN |
| 49152–49200 | UDP | TURN relay (медиа) |

---

## Запуск деплоя

Откройте PowerShell, перейдите в папку проекта и запустите:

```powershell
cd C:\Soft_pub\ZConect

powershell -ExecutionPolicy Bypass -File .\deploy\deploy_from_windows.ps1 `
  -VpsIp      YOUR_SERVER_IP `
  -SshUser    root `
  -TurnPass   "придумайте_пароль" `
  -PublicIpv4 YOUR_SERVER_IP
```

Дополнительные параметры:
```powershell
  -SshKeyPath C:\Users\you\.ssh\id_rsa       # SSH-ключ
  -DashboardAPIKey "my_secret_key"            # ключ для web-dashboard (по умолчанию: пустая строка = dashboard защищён только если задан)
```

После деплоя dashboard доступен по: `http://YOUR_IP:8080/dashboard?key=YOUR_KEY`

### Что делает скрипт (6 этапов)

```
[1/6] docker build   — собирает образ сервера на вашем ПК
[2/6] docker save    — сохраняет образ в .tar.gz
[3/6] tar            — упаковывает deploy/
[4/6] scp            — загружает образ + deploy/ на VPS
[5/6] SSH            — ставит Docker (если нет), запускает контейнеры
[6/6] cleanup        — удаляет временные файлы
```

По окончании скрипт выведет готовые настройки для клиента.

---

## Настройки клиента ZConnect

После деплоя в приложении (Настройки → Система):

| Поле | Значение |
|------|----------|
| Server API URL | `http://YOUR_SERVER_IP:8080` |
| WebSocket URL  | `ws://YOUR_SERVER_IP:8080/ws` |
| STUN URL       | `stun:YOUR_SERVER_IP:3478` |
| TURN URL       | `turn:YOUR_SERVER_IP:3478?transport=udp` |
| TURN Username  | `zconect` |
| TURN Password  | ваш TurnPass из скрипта |

---

## Обновление сервера

Если изменили код в `server/` — запустите тот же скрипт снова. Он пересоберёт образ и заменит контейнер.

---

## Полезные команды на VPS

```bash
# Статус контейнеров
cd /opt/zconect/deploy && sudo docker compose ps

# Логи сервера в реальном времени
sudo docker compose logs -f signaling

# Перезапуск
sudo docker compose restart signaling

# Проверка доступности
curl http://127.0.0.1:8080/healthz
```

---

## Если что-то пошло не так

**Ошибка на этапе `[1/6]`** — Docker Desktop не запущен. Запустите и повторите.

**Ошибка `Permission denied`** — неверный SSH-ключ или пользователь. Проверьте `-SshUser` и `-SshKeyPath`.

**`healthz` не отвечает** — порт 8080 не открыт в панели хостинга или занят другим процессом:
```bash
sudo ss -lntp | grep :8080
```

**TURN не работает (нет связи через NAT)** — проверьте что в панели хостинга открыты `3478/udp` и диапазон `49152-49200/udp`.
