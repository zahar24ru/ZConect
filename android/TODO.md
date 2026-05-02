# Android Viewer — TODO

## Deferred UX Improvements

### Polish
- [ ] **7. Быстрые действия** — кнопки `Win`, `Alt+Tab`, `Win+L`, `Super+D` в тулбаре
- [ ] **8. Snackbar уведомления** — "Copied to clipboard", "Quality changed", подтверждения
- [ ] **10. Таймер сессии** — "Connected: 00:15:23" в тулбаре
- [ ] **12. Блокировка ориентации** — замочек чтобы не крутился экран

### Features
- [ ] **11. Clipboard sync** — `dc-clipboard` инфраструктура уже есть, нужна UI интеграция
- [ ] **13. Звук с удалённого ПК** — WebRTC audio track
- [ ] **14. Sensitivity preview** — визуал при движении ползунка
- [ ] **16. Per-host settings** — разное качество/настройки на каждый ПК из Address Book

### Power users
- [ ] **15. Performance overlay** — packet loss, jitter, codec info (расширение stats overlay)
- [ ] **17. Haptic customization** — on/off + strength slider в Settings
- [ ] **18. Foreground service** — notification "Connected to PC-NAME" + защита от kill

## Protocol fixes (low priority)
- [ ] Определить почему screen_meta не всегда приходит автоматически (только при смене displayId)
- [ ] Убрать дублирование отправки (sendOnChannel шлёт на обе каналы — оба доходят до хоста, в логах видны повторные сообщения)
