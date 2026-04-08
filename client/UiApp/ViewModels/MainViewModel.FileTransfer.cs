using System.IO;
using System.Linq;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
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
        _dataChannelCoordinator?.SendFileRequestAsync(new FileRequestPayload { Path = path ?? string.Empty });
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
        _pendingDeleteRequestId = requestId;
        var dc = _dataChannelCoordinator;
        if (dc is null)
        {
            _pendingDeleteRequestId = null;
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
                    if (_pendingDeleteRequestId == requestId) _pendingDeleteRequestId = null;
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
                    items.Add(new RemoteFileItemPayload { Name = name, FullPath = dir, IsDirectory = true, Size = 0 });
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
                    items.Add(new RemoteFileItemPayload { Name = name, FullPath = file, IsDirectory = false, Size = fi.Length });
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
        if (string.IsNullOrEmpty(path) || _fileTransferService is null) return;
        if (!File.Exists(path))
        {
            _logService.Error("FileTransfer", "file_request_not_found", path);
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

}
