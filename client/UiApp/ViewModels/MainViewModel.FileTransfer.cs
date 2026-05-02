using System.IO;
using System.Linq;
using FileTransfer;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    // F-07 (external audit 2026-04-18): `GetFtRootJail` + `IsPathAllowedForFt`
    // были declared но НЕ использовались — handlers полагаются только на
    // `SafePath.IsSystemPath` (блокирует Windows/Program Files/system paths).
    //
    // Design decision (documented в CLIENT_SETTINGS.md): **viewer имеет полный
    // доступ к user profile** того пользователя, кто даёт remote session.
    // Логика: в remote desktop model viewer уже может mouse/keyboard в любом
    // окне/документе — ограничивать FT к jail'у создаёт false sense of
    // security (viewer просто скопирует файл через copy-paste clipboard или
    // drag-drop в remote file manager).
    //
    // Jail methods удалены. `FtRootJailPath` в ClientSettings — legacy поле,
    // оставлено для backward compat старых settings.json файлов (JSON
    // deserialization игнорирует unknown fields), но не используется.

    private Task SendFileAsync()
    {
        if (_fileTransferService is null)
        {
            StatusText = "Подключение не активно";
            OnPropertyChanged(nameof(StatusText));
            return Task.CompletedTask;
        }

        if (_incomingSaveDirHolder is not null && _outgoingTargetDirHolder is not null)
            FileTransferWindowRequested?.Invoke(_fileTransferService, _incomingSaveDirHolder, _outgoingTargetDirHolder, this);
        return Task.CompletedTask;
    }

    // ── Public API для правой панели FT (удалённая сторона) ──────────────────

    /// <summary>Запросить список каталога на удалённой стороне (для правой панели FT).</summary>
    public void RequestRemoteDirList(string path)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _pendingDirListRequestId = requestId;
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            RequestRemoteDirListFailed?.Invoke("Нет подключения.");
            return;
        }
        _logService.Info("FT", "dir_list_request_sent path=" + (path ?? "") + " requestId=" + requestId);
        _ = Task.Run(async () =>
        {
            try
            {
                await dc.SendDirListRequestAsync(new DirListRequestPayload { Path = path ?? string.Empty, RequestId = requestId });
            }
            catch (Exception ex)
            {
                _logService.Error("FT", "dir_list_request_send_failed", ex.Message);
                _uiContext.Post(_ => RequestRemoteDirListFailed?.Invoke(ex.Message), null);
            }
        });
    }

    /// <summary>Запросить файл с удалённой стороны (скачать в текущую папку приёма).</summary>
    public void RequestRemoteFile(string path)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _dataChannelCoordinator?.SendFileRequestAsync(new FileRequestPayload { Path = path ?? string.Empty, RequestId = requestId });
    }

    /// <summary>Запросить скачивание папки с удалённой стороны (рекурсивно).</summary>
    public void RequestRemoteFolderDownload(string path)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _logService.Info("FT", "folder_download_request_sent path=" + path + " requestId=" + requestId);
        _dataChannelCoordinator?.SendFolderDownloadRequestAsync(new FolderDownloadRequestPayload { Path = path ?? string.Empty, RequestId = requestId });
    }

    /// <summary>Запросить создание папки на удалённой стороне (для правой панели FT).</summary>
    public void RequestCreateRemoteFolder(string parentPath, string folderName)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _pendingCreateFolderRequestId = requestId;
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            _pendingCreateFolderRequestId = null;
            CreateFolderResponseReceived?.Invoke(new CreateFolderResponsePayload { RequestId = requestId, Success = false, Message = "Нет подключения." });
            return;
        }
        _logService.Info("FT", "create_folder_request_sent parent=" + (parentPath ?? "") + " name=" + (folderName ?? ""));
        _ = Task.Run(async () =>
        {
            try
            {
                await dc.SendCreateFolderRequestAsync(new CreateFolderRequestPayload
                {
                    ParentPath = parentPath ?? string.Empty,
                    FolderName = folderName ?? string.Empty,
                    RequestId = requestId
                });
            }
            catch (Exception ex)
            {
                _logService.Error("FT", "create_folder_request_send_failed", ex.Message);
                _uiContext.Post(_ =>
                {
                    if (_pendingCreateFolderRequestId == requestId) _pendingCreateFolderRequestId = null;
                    CreateFolderResponseReceived?.Invoke(new CreateFolderResponsePayload { RequestId = requestId, Success = false, Message = ex.Message });
                }, null);
            }
        });
    }

    /// <summary>Запросить удаление файла или папки на удалённой стороне.</summary>
    public void RequestRemoteDelete(string path)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _pendingDeleteRequestIds.TryAdd(requestId, 0);
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            _pendingDeleteRequestIds.TryRemove(requestId, out _);
            DeleteResponseReceived?.Invoke(new DeleteResponsePayload { RequestId = requestId, Success = false, Message = "Нет подключения." });
            return;
        }
        _logService.Info("FT", "delete_request_sent path=" + (path ?? ""));
        _ = Task.Run(async () =>
        {
            try
            {
                await dc.SendDeleteRequestAsync(new DeleteRequestPayload { Path = path ?? string.Empty, RequestId = requestId });
            }
            catch (Exception ex)
            {
                _logService.Error("FT", "delete_request_send_failed", ex.Message);
                _uiContext.Post(_ =>
                {
                    _pendingDeleteRequestIds.TryRemove(requestId, out byte _);
                    DeleteResponseReceived?.Invoke(new DeleteResponsePayload { RequestId = requestId, Success = false, Message = ex.Message });
                }, null);
            }
        });
    }

    // ── Обработчики входящих запросов от удалённой стороны (Host-сторона) ───

    private void OnDirListRequestReceived(DirListRequestPayload payload)
    {
        var path = payload?.Path ?? string.Empty;
        var requestId = payload?.RequestId ?? string.Empty;
        _logService.Info("FileTransfer", "dir_list_request_received path=" + (string.IsNullOrEmpty(path) ? "(корень)" : path));
        var items = new List<RemoteFileItemPayload>();
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        var name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.IsNullOrEmpty(name)) name = drive.Name;
                        items.Add(new RemoteFileItemPayload { Name = name, FullPath = drive.Name, IsDirectory = true, Size = 0 });
                    }
                    catch { /* skip inaccessible */ }
                }
                _dataChannelCoordinator?.SendDirListResponseAsync(new DirListResponsePayload { Path = path, Items = items, RequestId = requestId });
                _logService.Info("FileTransfer", "dir_list_response_sent path=(корень) items=" + items.Count);
                return;
            }
            if (!Directory.Exists(path))
            {
                _dataChannelCoordinator?.SendDirListResponseAsync(new DirListResponsePayload { Path = path, Items = items, RequestId = requestId });
                return;
            }
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                try
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;
                    var dirMs = new DateTimeOffset(Directory.GetLastWriteTime(dir)).ToUnixTimeMilliseconds();
                    items.Add(new RemoteFileItemPayload { Name = name, FullPath = dir, IsDirectory = true, Size = 0, DateModifiedMs = dirMs });
                }
                catch { /* skip inaccessible */ }
            }
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(name)) continue;
                    var fi = new FileInfo(file);
                    items.Add(new RemoteFileItemPayload { Name = name, FullPath = file, IsDirectory = false, Size = fi.Length, DateModifiedMs = new DateTimeOffset(fi.LastWriteTime).ToUnixTimeMilliseconds() });
                }
                catch { /* skip inaccessible */ }
            }
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "dir_list_failed", $"{path}: {ex.Message}");
        }
        _dataChannelCoordinator?.SendDirListResponseAsync(new DirListResponsePayload { Path = path, Items = items, RequestId = requestId });
        _logService.Info("FileTransfer", "dir_list_response_sent path=" + path + " items=" + items.Count);
    }

    private void OnFileRequestReceived(FileRequestPayload payload)
    {
        var path = payload?.Path ?? string.Empty;
        var requestId = payload?.RequestId ?? "request";

        // Bug fix 2026-04-19: раньше silent return когда service=null → viewer hang 30s
        // ждёт ответа (timeout в live test). Теперь любой invalid request получает
        // FileError response, viewer знает что download не пройдёт.
        if (string.IsNullOrEmpty(path))
        {
            _logService.Warn("FileTransfer", "file_request_empty_path");
            _dataChannelCoordinator?.SendFileErrorAsync(new WebRtcTransport.FileErrorPayload
                { TransferId = requestId, Code = "invalid_request", Message = "Путь не указан" });
            return;
        }
        if (_fileTransferService is null)
        {
            _logService.Warn("FileTransfer", "file_request_service_not_ready path=" + path);
            _dataChannelCoordinator?.SendFileErrorAsync(new WebRtcTransport.FileErrorPayload
                { TransferId = requestId, Code = "service_not_ready", Message = "FileTransfer сервис не инициализирован" });
            return;
        }
        // F-02: block system paths only (Windows, Program Files) — NOT jail-restricted
        // because viewer already sees these files via dir_list.
        if (SafePath.IsSystemPath(path))
        {
            _logService.Warn("FileTransfer", "file_request_rejected_system_path path=" + path);
            _dataChannelCoordinator?.SendFileErrorAsync(new WebRtcTransport.FileErrorPayload
                { TransferId = requestId, Code = "access_denied", Message = "Системный путь запрещён: " + Path.GetFileName(path) });
            return;
        }
        if (!File.Exists(path))
        {
            _logService.Error("FileTransfer", "file_request_not_found", path);
            _dataChannelCoordinator?.SendFileErrorAsync(new WebRtcTransport.FileErrorPayload
                { TransferId = requestId, Code = "not_found", Message = "Файл не найден: " + Path.GetFileName(path) });
            return;
        }
        _fileTransferService.EnqueueSend(path);
    }

    private void OnCreateFolderRequestReceived(CreateFolderRequestPayload payload)
    {
        var parent = payload?.ParentPath ?? string.Empty;
        var name = payload?.FolderName ?? string.Empty;
        var requestId = payload?.RequestId ?? string.Empty;
        _logService.Info("FileTransfer", "create_folder_request_received parent=" + parent + " name=" + name);
        try
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _dataChannelCoordinator?.SendCreateFolderResponseAsync(new CreateFolderResponsePayload { RequestId = requestId, Success = false, Message = "Недопустимое имя папки" });
                return;
            }
            var fullPath = Path.Combine(parent, name.Trim());
            // F-02: block system paths only.
            if (SafePath.IsSystemPath(fullPath))
            {
                _logService.Warn("FileTransfer", "create_folder_rejected_system_path path=" + fullPath);
                _dataChannelCoordinator?.SendCreateFolderResponseAsync(new CreateFolderResponsePayload { RequestId = requestId, Success = false, Message = "Создание в системных папках запрещено" });
                return;
            }
            Directory.CreateDirectory(fullPath);
            _dataChannelCoordinator?.SendCreateFolderResponseAsync(new CreateFolderResponsePayload { RequestId = requestId, Success = true });
            _logService.Info("FileTransfer", "create_folder_ok path=" + fullPath);
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "create_folder_failed", ex.Message);
            _dataChannelCoordinator?.SendCreateFolderResponseAsync(new CreateFolderResponsePayload { RequestId = requestId, Success = false, Message = ex.Message });
        }
    }

    private void OnDeleteRequestReceived(DeleteRequestPayload payload)
    {
        var path = payload?.Path ?? string.Empty;
        var requestId = payload?.RequestId ?? string.Empty;
        _logService.Info("FileTransfer", "delete_request_received path=" + path);
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                _dataChannelCoordinator?.SendDeleteResponseAsync(new DeleteResponsePayload { RequestId = requestId, Success = false, Message = "Путь не указан" });
                return;
            }
            // F-02: block system paths only.
            if (SafePath.IsSystemPath(path))
            {
                _logService.Warn("FileTransfer", "delete_rejected_system_path path=" + path);
                _dataChannelCoordinator?.SendDeleteResponseAsync(new DeleteResponsePayload { RequestId = requestId, Success = false, Message = "Удаление системных путей запрещено" });
                return;
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                _dataChannelCoordinator?.SendDeleteResponseAsync(new DeleteResponsePayload { RequestId = requestId, Success = true });
                _logService.Info("FileTransfer", "delete_ok file " + path);
                return;
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                _dataChannelCoordinator?.SendDeleteResponseAsync(new DeleteResponsePayload { RequestId = requestId, Success = true });
                _logService.Info("FileTransfer", "delete_ok dir " + path);
                return;
            }
            _dataChannelCoordinator?.SendDeleteResponseAsync(new DeleteResponsePayload { RequestId = requestId, Success = false, Message = "Не найден" });
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "delete_failed", ex.Message);
            _dataChannelCoordinator?.SendDeleteResponseAsync(new DeleteResponsePayload { RequestId = requestId, Success = false, Message = ex.Message });
        }
    }

    /// <summary>Host: handle folder download request — enumerate files recursively and send each.</summary>
    private void OnFolderDownloadRequestReceived(FolderDownloadRequestPayload payload)
    {
        var path = payload?.Path ?? string.Empty;
        var requestId = payload?.RequestId ?? string.Empty;
        _logService.Info("FileTransfer", "folder_download_request_received path=" + path);

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            _dataChannelCoordinator?.SendFolderDownloadResponseAsync(new FolderDownloadResponsePayload
                { RequestId = requestId, Success = false, Message = "Папка не найдена" });
            return;
        }
        if (SafePath.IsSystemPath(path))
        {
            _logService.Warn("FileTransfer", "folder_download_rejected_system_path path=" + path);
            _dataChannelCoordinator?.SendFolderDownloadResponseAsync(new FolderDownloadResponsePayload
                { RequestId = requestId, Success = false, Message = "Системный путь запрещён" });
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            _dataChannelCoordinator?.SendFolderDownloadResponseAsync(new FolderDownloadResponsePayload
                { RequestId = requestId, Success = true, FileCount = files.Count });
            _logService.Info("FileTransfer", $"folder_download_enqueue {files.Count} files from {path}");

            if (_fileTransferService is not null)
            {
                // Set batch info so receiver sees correct "X of Y" progress.
                long totalBytes = 0;
                foreach (var f in files) { try { totalBytes += new FileInfo(f).Length; } catch { } }
                _fileTransferService.SetBatchInfo(files.Count, totalBytes);

                var baseDir = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parentDir = Path.GetDirectoryName(baseDir) ?? baseDir;
                foreach (var file in files)
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(file);
                        var relativePath = Path.GetRelativePath(parentDir, fullPath);
                        _fileTransferService.EnqueueSend(fullPath, relativePath);
                    }
                    catch { /* skip inaccessible */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error("FileTransfer", "folder_download_failed", ex.Message);
            _dataChannelCoordinator?.SendFolderDownloadResponseAsync(new FolderDownloadResponsePayload
                { RequestId = requestId, Success = false, Message = ex.Message });
        }
    }

    /// <summary>Host: handle rename request from viewer.</summary>
    private void OnRenameRequestReceived(RenameRequestPayload payload)
    {
        var path = payload?.Path ?? string.Empty;
        var newName = payload?.NewName ?? string.Empty;
        var requestId = payload?.RequestId ?? string.Empty;
        _logService.Info("FileTransfer", "rename_request_received path=" + path + " newName=" + newName);
        try
        {
            if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _dataChannelCoordinator?.SendRenameResponseAsync(new RenameResponsePayload { RequestId = requestId, Success = false, Message = "Недопустимое имя" });
                return;
            }
            if (SafePath.IsSystemPath(path))
            {
                _dataChannelCoordinator?.SendRenameResponseAsync(new RenameResponsePayload { RequestId = requestId, Success = false, Message = "Системный путь" });
                return;
            }
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var newPath = Path.Combine(dir, newName.Trim());
            if (File.Exists(path))
                File.Move(path, newPath);
            else if (Directory.Exists(path))
                Directory.Move(path, newPath);
            else
            {
                _dataChannelCoordinator?.SendRenameResponseAsync(new RenameResponsePayload { RequestId = requestId, Success = false, Message = "Не найден" });
                return;
            }
            _dataChannelCoordinator?.SendRenameResponseAsync(new RenameResponsePayload { RequestId = requestId, Success = true });
        }
        catch (Exception ex)
        {
            _dataChannelCoordinator?.SendRenameResponseAsync(new RenameResponsePayload { RequestId = requestId, Success = false, Message = ex.Message });
        }
    }

    /// <summary>Viewer → host: request rename.</summary>
    public void RequestRemoteRename(string path, string newName)
    {
        var requestId = Guid.NewGuid().ToString("N");
        _dataChannelCoordinator?.SendRenameRequestAsync(new RenameRequestPayload { Path = path, NewName = newName, RequestId = requestId });
    }

    public event Action<RenameResponsePayload>? RenameResponseReceived;
}
