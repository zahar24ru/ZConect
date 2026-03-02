# Публикация ZConect на GitHub и использование другими

## Как выложить проект на GitHub

### 1. Инициализация репозитория (если ещё не git-репозиторий)

В корне проекта (`ZConect`):

```powershell
cd c:\Soft_pub\ZConect
git init
```

### 2. Проверка .gitignore

В корне уже есть `.gitignore`. Он исключает из коммита:

- папки сборки (`bin/`, `obj/`, `artifacts/`);
- логи (`logs.log`, `*.log`);
- секреты и окружение (`.env`, `deploy/.env`, `client-settings.json`);
- сгенерированные релиз-папки (`Zconnect-v1/`, `ZConect-v1.1/`);
- временные и системные файлы.

Проверьте, что в репозиторий не попадут пароли и локальные пути.

### 3. Первый коммит

```powershell
git add .
git status
# Убедитесь, что нет лишних файлов (bin, obj, .env, logs.log)
git commit -m "Initial commit: ZConect MVP"
```

### 4. Создание репозитория на GitHub

1. Зайдите на [github.com](https://github.com), войдите в аккаунт.
2. **New repository** → укажите имя (например, `ZConect`), описание, публичный репозиторий.
3. **Не** создавайте README, .gitignore или LICENSE — они уже есть локально.

### 5. Привязка и отправка

```powershell
git remote add origin https://github.com/<ваш-логин>/ZConect.git
git branch -M main
git push -u origin main
```

Готово: репозиторий доступен по ссылке `https://github.com/<ваш-логин>/ZConect`.

---

## Как другим пользоваться проектом

### Требования

- **Клиент (Windows):** Windows 10/11, .NET 8 SDK, Visual Studio 2022 или VS Build Tools (или только .NET 8 Runtime для запуска готового exe).
- **Сервер:** VPS с Ubuntu 24 LTS (или аналог), Docker и Docker Compose. Для NAT — настроенный STUN/TURN (например, coturn).
- **Опционально:** ffmpeg (для VP8 Probe и расширенной работы с видео).

### Клонирование

```bash
git clone https://github.com/<ваш-логин>/ZConect.git
cd ZConect
```

### Сборка клиента (Windows)

```powershell
cd client\UiApp
dotnet restore
dotnet build -c Release
# Исполняемый файл: client\UiApp\bin\Release\net8.0-windows\win-x64\UiApp.exe
```

Или открыть `client\UiApp\UiApp.sln` в Visual Studio и собрать проект.

### Развёртывание сервера

- **С Windows:** пошагово по `docs/DEPLOY_FROM_WINDOWS.md` (в т.ч. скрипт `deploy/deploy_from_windows.ps1`).
- **На Ubuntu (VPS):** по `docs/DEPLOY_UBUNTU24.md`; на сервере в каталоге `deploy` создать `.env` из `deploy/.env.example`, подставить свои `PUBLIC_IPV4`, `TURN_PASS` и т.д., затем `docker compose up -d`.

### Первый запуск для двух пользователей

1. На машине с сервером: развернуть сервер (см. выше).
2. На ПК A (host): запустить клиент → **Создать сессию** → записать коды 8+8.
3. На ПК B (viewer): запустить клиент → ввести коды → **Подключиться**.
4. В настройках обоих клиентов указать URL сервера и при необходимости STUN/TURN (см. `docs/CLIENT_SETTINGS.md`).

Дальнейшие сценарии и API описаны в `docs/ARCHITECTURE.md` и `docs/API.md`.
