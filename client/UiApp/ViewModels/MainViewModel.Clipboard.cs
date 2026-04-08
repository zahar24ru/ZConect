using System.Windows;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    private void StartClipboardSyncLoopIfNeeded()
    {
        if (Interlocked.Exchange(ref _clipboardLoopStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() => ClipboardSyncLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
    }

    private async Task ClipboardSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var text = await _uiContextInvokeAsync(() =>
                {
                    try
                    {
                        return Clipboard.ContainsText() ? (Clipboard.GetText() ?? string.Empty) : string.Empty;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                });

                if (!string.Equals(text, _lastObservedClipboardText, StringComparison.Ordinal))
                {
                    // Не пересылаем init-пинг как реальное содержимое буфера.
                    if (string.Equals(text, "zconect-init", StringComparison.Ordinal))
                    {
                        _lastObservedClipboardText = text;
                    }
                    else
                    {
                        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                        if (bytes > 0 && bytes <= ClipboardMaxBytes)
                        {
                            var isRecentRemoteEcho =
                                string.Equals(text, _lastAppliedRemoteClipboardText, StringComparison.Ordinal)
                                && (DateTime.UtcNow - _lastAppliedRemoteClipboardAtUtc).TotalMilliseconds < 1500;

                            if (isRecentRemoteEcho || string.Equals(text, _lastSentClipboardText, StringComparison.Ordinal))
                            {
                                _lastObservedClipboardText = text;
                            }
                            else
                            {
                                var dc = _dataChannelCoordinator;
                                if (dc is not null)
                                {
                                    try
                                    {
                                        var originId = _role == ConnectionRole.Viewer ? "viewer" : "host";
                                        await dc.SendClipboardAsync(new ClipboardTextPayload
                                        {
                                            Text = text,
                                            OriginPeerId = originId
                                        }, ct);
                                        _lastSentClipboardText = text;
                                        _lastObservedClipboardText = text;
                                        _logService.Debug("ClipboardSync", "clipboard_sent_len_" + text.Length);
                                    }
                                    catch
                                    {
                                        // Suppress — channel not open yet; will retry on next cycle
                                    }
                                }
                            }
                        }
                        else
                        {
                            _lastObservedClipboardText = text;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Update host capture stats (piggyback on clipboard poll loop).
            try { UpdateHostCaptureStats(); } catch { }

            try { await Task.Delay(ClipboardPollMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnClipboardReceived(ClipboardTextPayload payload)
    {
        if (payload is null)
        {
            return;
        }

        var text = payload.Text ?? string.Empty;
        if (string.Equals(text, "zconect-init", StringComparison.Ordinal))
        {
            _logService.Debug("ClipboardSync", "clipboard_init_ignored");
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > ClipboardMaxBytes)
        {
            _logService.Warn("ClipboardSync", "clipboard_received_too_large_" + bytes);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _uiContextInvokeAsync(() =>
                {
                    try { Clipboard.SetText(text); }
                    catch { /* ignore */ }
                    return true;
                });
                _lastAppliedRemoteClipboardText = text;
                _lastAppliedRemoteClipboardAtUtc = DateTime.UtcNow;
                _lastObservedClipboardText = text;
                _logService.Info("ClipboardSync", "clipboard_text_received_len_" + text.Length);
            }
            catch
            {
                // ignore
            }
        });
    }
}
