# Архитектура ZConect (MVP)

## 1) Общее

Система состоит из:

- Windows-клиента (Windows 10/11).
- Сервера сигналинга на VPS Ubuntu 24 LTS.
- STUN/TURN сервера (coturn) для прохождения NAT.
- Хранилища краткоживущих сессий (Redis).

Передача видео: VP8/VP9 (WebRTC software encoding).
Транспорт: WebRTC (P2P при возможности, TURN relay при необходимости).
Захват экрана: DXGI Output Duplication (primary) + GDI (fallback).
Сетевой стек MVP: IPv4.

## 2) Ключевые сценарии

### 2.1 Создание сессии (инициатор)

1. Клиент A нажимает "Создать сессию" и становится `host`.
2. Сервер создает сессию, генерирует:
   - `login_code`: 8 цифр
   - `pass_code`: 8 цифр
3. Клиент A показывает коды пользователю.
4. Клиент A ждет подключения участника B.
5. После подключения участника B `host` начинает шарить экран и принимать управление (mouse/keyboard).

### 2.2 Подключение к сессии (подключающийся)

1. Клиент B вводит `login_code` + `pass_code`.
2. Сервер валидирует коды и ограничения (TTL, попытки).
3. Сервер связывает A и B через signaling channel (WebSocket).
4. Клиенты выполняют SDP/ICE обмен.
5. Устанавливается медиа-канал + data channels.
6. Клиент B работает как `viewer`: открывает отдельное окно удалённого экрана, получает видео и отправляет input.

## 3) Клиентская архитектура

Каталоги клиента:

- `client/UiApp` — UI, состояния экрана, команды пользователя.
- `client/SessionClient` — REST/WS клиент сигналинга.
- `client/WebRtcTransport` — peer connection, SDP/ICE, data channels.
- `client/ScreenCapture` — захват экрана(ов), кадрирование, масштаб.
- `client/QualityController` — профили качества и адаптация bitrate/FPS.
- `client/InputHost` — отправка/применение мыши и клавиатуры.
- `client/ClipboardSync` — двунаправленная синхронизация буфера обмена.
- `client/FileTransfer` — простой обмен файлами чанками по data channel.
- `client/Logging` — единый фасад логирования в `logs.log`.

### 3.3 Конфигурация клиента (обязательно)

В UI добавляется страница/секция "Сеть и кодек" с параметрами:

- `ServerApiBaseUrl` (например, `https://example.com` или `http://1.2.3.4:8080` для теста).
- `WebSocketUrl` (если отличается от базового URL).
- `StunUrl` (например, `stun:1.2.3.4:3478`).
- `TurnUrl` (например, `turn:1.2.3.4:3478`).
- `TurnUsername`.
- `TurnPassword`.
- `PreferRelay` (опционально, форсировать TURN при проблемных сетях).
- `FfmpegPath` (путь к `ffmpeg.exe` для VP8 pipeline).

Все параметры валидируются в UI и сохраняются в локальный конфиг.

### 3.1 Модель потоков

- **UI поток**: только визуальная часть (WPF Dispatcher).
- **Capture поток**: выделенный `Thread` с high-precision loop (`Stopwatch` + `SpinWait`), DXGI/GDI захват + cursor compositing.
- **Encode**: WebRTC внутренний поток (VP8/VP9 encoding из `ExternalVideoTrackSource` callback).
- **Network поток**: signaling WebSocket + WebRTC callbacks.
- **Input поток**: события мыши/клавиатуры (`SendInput`/`SetCursorPos`).
- **File transfer поток**: отправка чанков файла по data channel.

Взаимодействие модулей через четкие интерфейсы и события.

### 3.2 Управление доступом (опционально)

В UI есть переключатель:

- `Запрашивать подтверждение` = On: удаленная сторона должна подтвердить вход.
- `Запрашивать подтверждение` = Off: unattended режим, вход без подтверждения.

## 4) Серверная архитектура

Каталоги сервера:

- `server/signaling` — HTTP + WebSocket сервер.
- `server/session` — логика сессий и кодов (8+8), TTL, состояние.
- `server/security` — rate limit, лимит попыток, блокировки.
- `server/logging` — серверный logger и корреляция по session_id.

### 4.1 Состояния сессии

- `CREATED` — коды выданы, второй клиент еще не присоединился.
- `PAIRING` — второй клиент проходит проверку.
- `CONNECTED` — идет активная сессия.
- `CLOSED` — завершена пользователем/по ошибке/по TTL.

## 5) Data channels

Отдельные логические каналы:

- `dc-input` — события мыши/клавиатуры.
- `dc-clipboard` — sync буфера обмена.
- `dc-file` — обмен файлами.
- `dc-control` — служебные команды (quality-change, screen_meta, display switching, ping/pong RTT).

Текущий дополнительный control-сценарий:

- `viewer` может отправить в `dc-control` запрос изменения видео-настроек host:
  - `qualityPreset`
  - `displayMode`
  - `displayId`
  - `quickReconnect`
- host применяет захват с новыми параметрами и отправляет обновленный `screen_meta`.
- при `quickReconnect=true` host инициирует быструю renegotiation (`offer/answer`) без полного разрыва пользовательской сессии.

## 6) Видео и качество

### 6.1 Профили качества

| Профиль | Разрешение | FPS | Битрейт |
|---------|-----------|-----|---------|
| Extra Low | 640×360 | 20 | 400 kbps |
| Low | 854×480 | 20 | 900 kbps |
| Medium | 1280×720 | 30 | 2200 kbps |
| High | 1920×1080 | 30 | 4200 kbps |
| Auto | = Medium (начальный) | адаптивный | адаптивный |

При выборе профиля нативное разрешение дисплея масштабируется вниз с сохранением пропорций.
Bitrate hints передаются в WebRTC через `PeerConnection.SetBitrate(min=50%, start=100%, max=120%)`.

### 6.2 Захват экрана

**DXGI Output Duplication API** — основной метод. Direct3D 11 GPU-захват без GDI. Для каждого монитора создаётся `IDXGIOutputDuplication`, кадр копируется в staging-текстуру и маппится для CPU-чтения.

**DXGI Multi-Output Stitching** — при выборе "All" мониторов. Каждый выход (`IDXGIOutput`) захватывается независимо через собственный D3D11 device + OutputDuplication. Кадры сшиваются в единый буфер по desktop-координатам (`DxgiMultiOutputCapture`).

**GDI CopyFromScreen** — fallback при недоступности DXGI (RDP-сессии, некоторые VM).

### 6.3 Оптимизации захвата

**Dirty rects** — DXGI возвращает список изменённых прямоугольников (`GetFrameDirtyRects`). Если dirty area < 50% экрана — копируются только изменённые регионы вместо полного кадра. Экономия CPU ~30–50% при типичной работе.

**Double buffering** — два pinned-буфера (`GC.AllocateArray<byte>(pinned: true)`). Capture-поток пишет в back buffer, WebRTC-энкодер читает из front buffer через `GCHandle.AddrOfPinnedObject()`. Zero-copy к энкодеру.

**Adaptive FPS** — при отсутствии изменений на экране (DXGI не отдал кадр / GDI pixel sampling) FPS снижается: base → base/2 → base/4. При появлении активности мгновенно возвращается к базовому.

**High-precision capture loop** — выделенный поток `Thread` с `Stopwatch` + `SpinWait` (~1ms точность) вместо `System.Timers.Timer` (~15ms jitter). Приоритет `AboveNormal`.

### 6.4 Курсор

DXGI Output Duplication не захватывает курсор. Композитинг вручную:
1. `GetCursorInfo` → получение позиции и handle курсора
2. `DrawIconEx` → рендер в кэшированный bitmap 48×48 (пересоздаётся только при смене `hCursor`)
3. Alpha-blending напрямую в capture-буфер (unsafe pointer arithmetic)

### 6.5 Автоадаптация качества

Фоновый цикл (1.5 сек) опрашивает `PeerConnection.GetSimpleStatsAsync()` — `VideoSenderStats` (BytesSent, FramesSent, FramesEncoded):
- **Деградация**: >15% потерь кадров ИЛИ bitrate < 50% от целевого → 3 подряд плохих замера → понижение
- **Улучшение**: <3% потерь И bitrate ≥ 85% → 8 подряд хороших замеров → повышение
- Cooldown 5 замеров после каждого переключения
- Лестница: Extra Low ↔ Low ↔ Medium ↔ High

### 6.6 Viewer-side рендеринг

`WriteableBitmap.Lock()` → `unsafe Buffer.MemoryCopy` → `AddDirtyRect` → `Unlock()`.
Буферы кадров переиспользуются через `ConcurrentBag<byte[]>` pool (до 4 штук) — без аллокаций в горячем пути.

### 6.7 Мониторинг

- **Viewer overlay**: `1280×720  30 fps  2.1 Mbps  RTT: 25ms`
- **Host stats**: `Захват: 30 fps  отправлено: 28  пропущено: 7%  RTT: 25ms`
- **RTT**: ping/pong через Data Channel каждые 3 сек, экспоненциальное сглаживание (EMA 0.7/0.3)

### 6.8 Выбор дисплея

В UI единый список дисплеев: `DISPLAY1`, `DISPLAY2`, ..., `All`.
- Конкретный дисплей → DXGI single output capture
- All → DXGI multi-output stitching (GDI fallback)
- Viewer может переключать дисплей host "на лету" через `dc-control`

## 7) Структура клиентского кода

`MainViewModel` разбит на partial-файлы:

| Файл | Ответственность |
|------|----------------|
| `MainViewModel.cs` | поля, конструктор, свойства настроек, `SaveSettings`, `OnPropertyChanged` |
| `MainViewModel.Connection.cs` | lifecycle сессии, `ConnectWsAsync`, авто-ICE, signaling-обработчики |
| `MainViewModel.Ice.cs` | мониторинг ICE, `InferRouteType`, `IceTypeRank`, UI-индикация |
| `MainViewModel.Video.cs` | захват/рендер видео, управление дисплеями, FFmpeg probe |
| `MainViewModel.Clipboard.cs` | clipboard sync loop и приём |
| `MainViewModel.FileTransfer.cs` | отправка/приём файлов, SHA-256, прогресс |
| `MainViewModel.Input.cs` | ввод viewer-side, cursor sync, coordinate mapping |
| `MainViewModel.Uac.cs` | политика UAC Secure Desktop |

Команды (`RelayCommand`, `AsyncRelayCommand`) вынесены в `UiApp/Commands/RelayCommand.cs`.

## 8) Известные ограничения сервера

- **WebSocket keepalive**: ping отправляется каждые 30 сек, read deadline — 60 сек. Подходит для стабильных соединений.
- **Concurrent writes**: защищены per-peer `writeMu` мьютексом (gorilla/websocket не потокобезопасен).
- **In-memory sessions**: Redis сконфигурирован в `docker-compose.yml`, но не используется в коде; данные сбрасываются при рестарте сервера.
- **ws_token**: stub `"todo-short-lived-token"`, аутентификация WS-подключения не реализована.

## 10) Логи

Все клиентские события пишутся в `logs.log`.
Подробности формата: `docs/LOGGING.md`.

## 8) Ограничения MVP

- Только IPv4.
- Без системного Windows-сервиса.
- UAC/elevated окна могут иметь ограничения в управлении.

## 9) Нефункциональные требования

- Отзывчивый интерфейс и простая UX-модель (один основной экран).
- Разбитие кода на модули без смешивания ответственности.
- Минимальная сложность деплоя сервера (docker compose + env).
