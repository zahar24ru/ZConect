# Release bundle: `Zconnect-v1`

Цель: собрать в одну папку всё, что нужно для теста/передачи:

- `client` — готовая сборка Windows клиента (Release publish).
- `server + deploy` — серверная часть для запуска на Ubuntu 24 LTS через Docker Compose.
- `docs` — документация и инструкции.

## 1) Как собрать бандл на Windows

Из корня проекта:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build_release_bundle.ps1
```

Скрипт создаст папку `.\Zconnect-v1` (и опционально `.\Zconnect-v1.zip`).

## 2) Что внутри `Zconnect-v1`

- `Zconnect-v1\client\win-x64\` — готовый запуск клиента (`UiApp.exe`).
- `Zconnect-v1\deploy\` — `docker-compose.yml` и `.env` (создаётся на VPS скриптом деплоя).
- `Zconnect-v1\server\` — исходники сервера (используются как build context для Docker).
- `Zconnect-v1\docs\` — актуальная документация.
- `Zconnect-v1\src\` — “снимок” исходников V1 (client/server/deploy/docs/tools без `bin/obj/.vs`), чтобы было проще откатиться/сравнить.

## 3) Быстрый тест (2 ноутбука)

1) Развернуть сервер: `docs/DEPLOY_FROM_WINDOWS.md` (одна команда из Windows).
2) На ноутбуке A нажать `Создать сессию` (это `host`).
3) На ноутбуке B ввести коды и нажать `Подключиться` (это `viewer`).
4) На ноутбуке B откроется окно `Удалённый экран`:
   - кликните в окно для фокуса
   - проверьте видео, мышь, клавиатуру

