using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using UiApp.Models;
using FileTransfer;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed class SortOption
{
    public string Key { get; init; } = "name";
    public string Label { get; init; } = "По имени";
}

public sealed class BreadcrumbSegment
{
    public string Label { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsLast { get; init; }
}

public sealed class FileTransferWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const int DirListTimeoutSeconds = 15;
    private const int InitialRequestDelayMs = 1500;

    public static List<SortOption> SortOptions { get; } = new()
    {
        new SortOption { Key = "name", Label = "По имени" },
        new SortOption { Key = "size", Label = "По размеру" },
        new SortOption { Key = "ext", Label = "По типу" },
    };

    private SortOption _leftSortOption = SortOptions[0];
    private SortOption _rightSortOption = SortOptions[0];

    private readonly FileTransferService _fileTransferService;
    private readonly IncomingSaveDirHolder _incomingSaveDirHolder;
    private readonly OutgoingTargetDirHolder _outgoingTargetDirHolder;
    private readonly MainViewModel? _mainViewModel;
    private string _leftPath = string.Empty;
    private string _rightPath = string.Empty;
    private bool _rightPanelLoading;
    private DispatcherTimer? _dirListResponseTimer;
    private string _diagnosticText = "";
    private string _statusText = "Передача в обе стороны: host ↔ клиент. Выберите файлы слева и нажмите Send →.";
    private string _progressText = string.Empty;
    private double _progressValue;
    private bool _progressVisible;

    public FileTransferWindowViewModel(FileTransferService fileTransferService, IncomingSaveDirHolder incomingSaveDirHolder, OutgoingTargetDirHolder outgoingTargetDirHolder, MainViewModel? mainViewModel = null)
    {
        _fileTransferService = fileTransferService;
        _incomingSaveDirHolder = incomingSaveDirHolder;
        _outgoingTargetDirHolder = outgoingTargetDirHolder;
        _mainViewModel = mainViewModel;
        LeftItems = new ObservableCollection<FileItem>();
        RightItems = new ObservableCollection<FileItem>();
        SelectedLeftItems = new ObservableCollection<FileItem>();
        SelectedRightItems = new ObservableCollection<FileItem>();

        RefreshLeftCommand = new RelayCommand(RefreshLeft);
        RefreshRightCommand = new RelayCommand(RefreshRight);
        ParentLeftCommand = new RelayCommand(ParentLeft);
        ParentRightCommand = new RelayCommand(ParentRight);
        HomeLeftCommand = new RelayCommand(HomeLeft);
        HomeRightCommand = new RelayCommand(HomeRight);
        SendToRemoteCommand = new RelayCommand(SendSelectedFromLeftToRemote);
        SendFromRightToRemoteCommand = new RelayCommand(SendSelectedFromRightToRemote);
        CreateFolderLeftCommand = new RelayCommand(() => CreateFolderInline(true));
        CreateFolderRightCommand = new RelayCommand(() => CreateFolderInline(false));
        ShowPropertiesLeftCommand = new RelayCommand(ShowPropertiesLeft);
        ShowPropertiesRightCommand = new RelayCommand(ShowPropertiesRight);
        DeleteLeftCommand = new RelayCommand(DeleteLeft);
        DeleteRightCommand = new RelayCommand(DeleteRight);
        ClearTransfersCommand = new RelayCommand(ClearCompletedTransfers);
        RenameLeftCommand = new RelayCommand(() => StartInlineRename(true));
        RenameRightCommand = new RelayCommand(() => StartInlineRename(false));
        CopyPathLeftCommand = new RelayCommand(() => CopySelectedPath(true));
        CopyPathRightCommand = new RelayCommand(() => CopySelectedPath(false));

        QueueProgress = new TransferQueueProgress
        {
            PauseAllCommand = new RelayCommand(() => { _fileTransferService.PauseAll(); QueueProgress.IsPaused = true; }),
            ResumeAllCommand = new RelayCommand(() => { _fileTransferService.ResumeAll(); QueueProgress.IsPaused = false; }),
            CancelAllCommand = new RelayCommand(() => { _fileTransferService.CancelAll(); QueueProgress.Reset(); _incomingRegistered.Clear(); }),
        };

        _fileTransferService.TransferProgress += OnTransferProgress;
        _fileTransferService.TransferCompleted += OnTransferCompleted;
        _fileTransferService.TransferFailed += OnTransferFailed;
        _fileTransferService.QueueChanged += OnQueueChanged;

        SetLeftPath("");
        if (_mainViewModel != null && _mainViewModel.IsRemoteFilePanelAvailable)
        {
            _mainViewModel.RemoteDirListReceived += OnRemoteDirListReceived;
            _mainViewModel.RequestRemoteDirListFailed += OnRequestRemoteDirListFailed;
            _mainViewModel.CreateFolderResponseReceived += OnCreateFolderResponseReceived;
            _mainViewModel.DeleteResponseReceived += OnDeleteResponseReceived;
            _mainViewModel.RenameResponseReceived += OnRenameResponseReceived;
            RightPath = "…";
            RightPanelLoading = true;
            SetDiagnostic("Лев=локально | Прав=удалённо (ожидание списка дисков)");
            _mainViewModel.LogFromFileTransfer("FT open: rightPanelRemote=true IsAvailable=true, delayed request root");
            StartDelayedInitialRequest();
        }
        else
        {
            var receivedDir = string.IsNullOrEmpty(_incomingSaveDirHolder.Path)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConectReceived")
                : _incomingSaveDirHolder.Path;
            SetDiagnostic("Лев=локально | Прав=локально (папка полученных) IsRemoteAvailable=" + (_mainViewModel?.IsRemoteFilePanelAvailable ?? false));
            _mainViewModel?.LogFromFileTransfer("FT open: rightPanelRemote=false right=local path=" + receivedDir);
            SetRightPath(receivedDir);
        }
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        set { _diagnosticText = value ?? ""; OnPropertyChanged(); }
    }

    private void SetDiagnostic(string msg)
    {
        DiagnosticText = msg;
        _mainViewModel?.LogFromFileTransfer("FT diag: " + msg);
    }

    /// <summary>Правая панель показывает удалённую сторону (другой ПК). False при mock/loopback — тогда справа папка «полученные».</summary>
    public bool IsRightPanelRemote => _mainViewModel != null && _mainViewModel.IsRemoteFilePanelAvailable;

    /// <summary>Когда false — справа папка полученных (нет подключения к другому ПК или mock).</summary>
    public string RightPanelTitle => IsRightPanelRemote ? "Удалённая сторона (другой ПК)" : "Папка полученных (подключение не к другому ПК)";

    public bool RightPanelLoading
    {
        get => _rightPanelLoading;
        set { _rightPanelLoading = value; OnPropertyChanged(); }
    }

    public ObservableCollection<FileItem> LeftItems { get; }
    public ObservableCollection<FileItem> RightItems { get; }
    public ObservableCollection<FileItem> SelectedLeftItems { get; }
    public ObservableCollection<FileItem> SelectedRightItems { get; }
    public ObservableCollection<TransferDisplayItem> Transfers { get; } = new();
    public TransferQueueProgress QueueProgress { get; private set; } = null!;
    public ObservableCollection<BreadcrumbSegment> LeftBreadcrumbs { get; } = new();
    public ObservableCollection<BreadcrumbSegment> RightBreadcrumbs { get; } = new();

    /// <summary>true = левая панель, false = правая. Kept for backward compat with code-behind OnRequestNewFolder.</summary>
    #pragma warning disable CS0067
    public event Action<bool>? RequestNewFolder;
    #pragma warning restore CS0067

    /// <summary>Показать диалог свойств (текст с путём и размером).</summary>
    public event Action<string>? ShowPropertiesDialogRequested;

    /// <summary>Запрос подтверждения удаления. View показывает «Точно удалить?» и при «Да» вызывает ConfirmDelete.</summary>
    public event Action<System.Collections.Generic.IEnumerable<FileItem>, bool>? RequestDeleteConfirmation;
    /// <summary>Request inline rename on the specified panel. View will show TextBox overlay. Params: isLeft, item, callback(newName).</summary>
    public event Action<bool, FileItem, Action<string>>? RequestInlineEdit;

    public string LeftPath
    {
        get => _leftPath;
        set
        {
            _leftPath = value ?? string.Empty;
            OnPropertyChanged();
            UpdateBreadcrumbs(LeftBreadcrumbs, _leftPath);
            SyncIncomingSaveDirFromLeftPanel();
        }
    }

    public string RightPath
    {
        get => _rightPath;
        set
        {
            _rightPath = value;
            // FT-UX-02 fix: also reset holder on empty value (root) to prevent stale target.
            if (IsRightPanelRemote && value != "…")
                _outgoingTargetDirHolder.Path = value ?? string.Empty;
            OnPropertyChanged();
            UpdateBreadcrumbs(RightBreadcrumbs, _rightPath);
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value; OnPropertyChanged(); }
    }

    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    public bool ProgressVisible
    {
        get => _progressVisible;
        set { _progressVisible = value; OnPropertyChanged(); }
    }

    public ICommand RefreshLeftCommand { get; }
    public ICommand RefreshRightCommand { get; }
    public ICommand ParentLeftCommand { get; }
    public ICommand ParentRightCommand { get; }
    public ICommand HomeLeftCommand { get; }
    public ICommand HomeRightCommand { get; }
    public ICommand SendToRemoteCommand { get; }
    public ICommand SendFromRightToRemoteCommand { get; }
    public ICommand CreateFolderLeftCommand { get; }
    public ICommand CreateFolderRightCommand { get; }
    public ICommand ShowPropertiesLeftCommand { get; }
    public ICommand ShowPropertiesRightCommand { get; }
    public ICommand DeleteLeftCommand { get; }
    public ICommand DeleteRightCommand { get; }
    public ICommand ClearTransfersCommand { get; }
    public ICommand RenameLeftCommand { get; }
    public ICommand RenameRightCommand { get; }
    public ICommand CopyPathLeftCommand { get; }
    public ICommand CopyPathRightCommand { get; }

    private string _viewMode = "Details";
    public string ViewMode
    {
        get => _viewMode;
        set { _viewMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLargeIcons)); }
    }
    /// <summary>True when large icons mode — ListBox uses WrapPanel.</summary>
    public bool IsLargeIcons => ViewMode == "LargeIcons";

    public SortOption LeftSortOption
    {
        get => _leftSortOption;
        set { _leftSortOption = value; OnPropertyChanged(); RefreshLeft(); }
    }

    public SortOption RightSortOption
    {
        get => _rightSortOption;
        set
        {
            _rightSortOption = value;
            OnPropertyChanged();
            if (!IsRightPanelRemote)
            {
                RefreshRight();
            }
            else
            {
                // Bug fix 2026-04-18: в remote режиме RefreshRight() отправил бы
                // dir_list_request по pipe, но результата можно ждать 100-500ms и
                // UX ломается (sort dropdown не отзывается). Просто re-sort
                // существующий RightItems in-place — данные уже есть.
                var sorted = ApplySort(RightItems.ToList(), _rightSortOption).ToList();
                RightItems.Clear();
                foreach (var item in sorted) RightItems.Add(item);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CreateFolderInLeft(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrEmpty(LeftPath)) return;
        try
        {
            var path = Path.Combine(LeftPath, folderName.Trim());
            Directory.CreateDirectory(path);
            RefreshLeft();
            StatusText = "Папка создана (левая): " + folderName;
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка создания папки: " + ex.Message;
        }
    }

    public void CreateFolderInRight(string folderName)
    {
        if (IsRightPanelRemote)
        {
            if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrEmpty(RightPath))
            {
                StatusText = "Укажите имя папки и откройте папку на удалённой стороне.";
                return;
            }
            StatusText = "Создание папки на удалённой стороне…";
            _mainViewModel?.RequestCreateRemoteFolder(RightPath, folderName.Trim());
            return;
        }
        if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrEmpty(RightPath)) return;
        try
        {
            if (!Directory.Exists(RightPath))
                Directory.CreateDirectory(RightPath);
            var path = Path.Combine(RightPath, folderName.Trim());
            Directory.CreateDirectory(path);
            RefreshRight();
            StatusText = "Папка создана (правая): " + folderName;
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка создания папки: " + ex.Message;
        }
    }

    public void SetLeftPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            LeftPath = "";
            RefreshLeft();
            return;
        }
        if (!Directory.Exists(path)) return;
        LeftPath = path;
        SyncIncomingSaveDirFromLeftPanel();
        RefreshLeft();
    }

    /// <summary>Папка приёма входящих файлов = текущая папка левой панели. Вызывать при загрузке/активации окна и при смене пути. Папка может ещё не существовать — при сохранении файла она будет создана.</summary>
    public void SyncIncomingSaveDirFromLeftPanel()
    {
        if (string.IsNullOrEmpty(LeftPath)) return;
        var path = LeftPath.Trim();
        if (path.Length == 0) return;
        _incomingSaveDirHolder.Path = path;
        _mainViewModel?.SetIncomingSaveDirFromFileTransfer(path);
    }

    public void SetRightPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            RightPath = "";
            if (IsRightPanelRemote)
            {
                RightPanelLoading = true;
                StartRequestWithTimeout("");
            }
            else
                RefreshRight();
            return;
        }
        if (IsRightPanelRemote)
        {
            RightPath = path;
            RightPanelLoading = true;
            StartRequestWithTimeout(path);
            return;
        }
        RightPath = path;
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        RefreshRight();
    }

    public void NavigateLeft(FileItem item)
    {
        if (item?.IsDirectory != true) return;
        SetLeftPath(item.FullPath);
    }

    public void NavigateRight(FileItem item)
    {
        if (item?.IsDirectory != true) return;
        if (IsRightPanelRemote)
        {
            RightPanelLoading = true;
            StartRequestWithTimeout(item.FullPath);
            return;
        }
        SetRightPath(item.FullPath);
    }

    private void StartDelayedInitialRequest()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(InitialRequestDelayMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            StartRequestWithTimeout("");
        };
        timer.Start();
    }

    private void StartRequestWithTimeout(string path)
    {
        var effectivePath = path == "…" ? "" : (path ?? "");
        SetDiagnostic("Лев=локально | Прав=удалённо (запрос " + effectivePath + ")");
        _mainViewModel?.LogFromFileTransfer("FT request path=" + effectivePath);
        StopDirListResponseTimer();
        _dirListResponseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(DirListTimeoutSeconds)
        };
        _dirListResponseTimer.Tick += (_, _) =>
        {
            StopDirListResponseTimer();
            RightPanelLoading = false;
            StatusText = "Нет ответа от удалённой стороны. Нажмите ⟳ для повтора.";
            SetDiagnostic("Лев=локально | Прав=удалённо (таймаут, ответ не получен)");
            _mainViewModel?.LogFromFileTransfer("FT timeout waiting for path=" + effectivePath);
        };
        _dirListResponseTimer.Start();
        _mainViewModel!.RequestRemoteDirList(effectivePath);
    }

    private void StopDirListResponseTimer()
    {
        _dirListResponseTimer?.Stop();
        _dirListResponseTimer = null;
    }

    private void OnCreateFolderResponseReceived(CreateFolderResponsePayload payload)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (payload.Success)
            {
                RefreshRight();
                StatusText = "Папка на удалённой стороне создана.";
            }
            else
            {
                StatusText = "Ошибка создания папки на удалённой стороне: " + (payload.Message ?? "неизвестная ошибка");
            }
        });
    }

    private void OnRequestRemoteDirListFailed(string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            StopDirListResponseTimer();
            RightPanelLoading = false;
            StatusText = "Не удалось отправить запрос: " + message;
            SetDiagnostic("Лев=локально | Прав=ошибка отправки: " + message);
            _mainViewModel?.LogFromFileTransfer("FT send failed: " + message);
        });
    }

    private void OnRemoteDirListReceived(DirListResponsePayload payload)
    {
        var path = payload.Path ?? "";
        var items = payload.Items ?? new List<RemoteFileItemPayload>();
        _mainViewModel?.LogFromFileTransfer("FT received dir_list_response path=" + path + " items=" + items.Count);
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            StopDirListResponseTimer();
            RightPanelLoading = false;
            RightPath = path;
            RightItems.Clear();
            var remoteItems = items.Select(r => new FileItem
            {
                Name = r.Name,
                FullPath = r.FullPath,
                IsDirectory = r.IsDirectory,
                Size = r.Size,
                DateModified = r.DateModifiedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.DateModifiedMs).LocalDateTime : default
            });
            foreach (var item in ApplySort(remoteItems, _rightSortOption))
                RightItems.Add(item);
            SetDiagnostic("Лев=локально | Прав=удалённо (получено " + items.Count + " элементов, путь " + path + ")");
        });
    }

    public void SelectionLeftChanged(IEnumerable<FileItem> selected)
    {
        SelectedLeftItems.Clear();
        foreach (var item in selected ?? Array.Empty<FileItem>())
            SelectedLeftItems.Add(item);
        OnPropertyChanged(nameof(SendRightTooltip));
        UpdateSelectionInfo();
    }

    public void SelectionRightChanged(IEnumerable<FileItem> selected)
    {
        SelectedRightItems.Clear();
        foreach (var item in selected ?? Array.Empty<FileItem>())
            SelectedRightItems.Add(item);
        OnPropertyChanged(nameof(SendLeftTooltip));
        UpdateSelectionInfo();
    }

    private string _selectionInfoText = string.Empty;
    public string SelectionInfoText { get => _selectionInfoText; set { _selectionInfoText = value; OnPropertyChanged(); } }

    private void UpdateSelectionInfo()
    {
        var left = SelectedLeftItems;
        var right = SelectedRightItems;
        var items = left.Count > 0 ? left : right;
        if (items.Count == 0) { SelectionInfoText = string.Empty; return; }
        var files = items.Count(x => !x.IsDirectory);
        var dirs = items.Count(x => x.IsDirectory);
        var totalSize = items.Where(x => !x.IsDirectory).Sum(x => x.Size);
        var parts = new List<string>();
        if (files > 0) parts.Add($"{files} файл(ов)");
        if (dirs > 0) parts.Add($"{dirs} папок");
        SelectionInfoText = $"Выбрано: {string.Join(", ", parts)}, {FormatSize(totalSize)}";
    }

    private void CopySelectedPath(bool isLeft)
    {
        var item = (isLeft ? SelectedLeftItems : SelectedRightItems).FirstOrDefault();
        if (item is null) return;
        try { System.Windows.Clipboard.SetText(item.FullPath); StatusText = "Путь скопирован"; }
        catch { }
    }

    /// <summary>Execute rename on the specified panel. isLeft: local panel; false: right panel (local or remote).</summary>
    private void StartInlineRename(bool isLeft)
    {
        var item = (isLeft ? SelectedLeftItems : SelectedRightItems).FirstOrDefault();
        if (item is null) return;
        RequestInlineEdit?.Invoke(isLeft, item, newName => ExecuteRename(item.FullPath, newName, isLeft));
    }

    /// <summary>Create a new folder inline — inserts a temp item, user types name, then creates.</summary>
    public void CreateFolderInline(bool isLeft)
    {
        var parentPath = isLeft ? LeftPath : _rightPath;
        if (string.IsNullOrEmpty(parentPath)) return;
        var placeholder = new FileItem { Name = "Новая папка", FullPath = "", IsDirectory = true };
        var items = isLeft ? LeftItems : RightItems;
        items.Insert(0, placeholder);
        RequestInlineEdit?.Invoke(isLeft, placeholder, newName =>
        {
            items.Remove(placeholder);
            if (string.IsNullOrWhiteSpace(newName)) { if (isLeft) RefreshLeft(); else RefreshRight(); return; }
            if (isLeft || !IsRightPanelRemote)
            {
                try { Directory.CreateDirectory(Path.Combine(parentPath, newName.Trim())); }
                catch (Exception ex) { StatusText = "Ошибка: " + ex.Message; }
                if (isLeft) RefreshLeft(); else RefreshRight();
            }
            else
            {
                _mainViewModel?.RequestCreateRemoteFolder(parentPath, newName.Trim());
            }
        });
    }

    /// <summary>Enqueue a file for sending (for drag&drop from Explorer).</summary>
    public void EnqueueSendExternal(string filePath) => _fileTransferService.EnqueueSend(filePath);
    /// <summary>Enqueue a folder for sending (for drag&drop from Explorer).</summary>
    public void EnqueueSendFolderExternal(string folderPath) => _fileTransferService.EnqueueSendFolder(folderPath);

    public void ExecuteRename(string oldPath, string newName, bool isLeft)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText = "Недопустимое имя файла";
            return;
        }
        if (isLeft || !IsRightPanelRemote)
        {
            // Local rename
            try
            {
                var dir = Path.GetDirectoryName(oldPath) ?? string.Empty;
                var newPath = Path.Combine(dir, newName.Trim());
                if (File.Exists(oldPath)) File.Move(oldPath, newPath);
                else if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
                if (isLeft) RefreshLeft(); else RefreshRight();
                StatusText = "Переименовано: " + newName;
            }
            catch (Exception ex) { StatusText = "Ошибка: " + ex.Message; }
        }
        else
        {
            // Remote rename
            _mainViewModel?.RequestRemoteRename(oldPath, newName);
            StatusText = "Запрос переименования...";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <summary>Dynamic tooltip for the right-arrow (send left selection to remote).</summary>
    public string SendRightTooltip
    {
        get
        {
            var count = SelectedLeftItems.Count;
            if (count == 0) return "Передать выбранные файлы \u2192";
            if (count == 1) return $"Передать \"{SelectedLeftItems[0].Name}\" \u2192";
            return $"Передать {count} элемент(ов) \u2192";
        }
    }

    /// <summary>Dynamic tooltip for the left-arrow (send right selection to local).</summary>
    public string SendLeftTooltip
    {
        get
        {
            var count = SelectedRightItems.Count;
            if (count == 0) return "\u2190 Передать выбранные файлы";
            if (count == 1) return $"\u2190 Передать \"{SelectedRightItems[0].Name}\"";
            return $"\u2190 Передать {count} элемент(ов)";
        }
    }

    private void RefreshLeft()
    {
        LeftItems.Clear();
        if (string.IsNullOrEmpty(LeftPath))
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        var name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.IsNullOrEmpty(name)) name = drive.Name;
                        LeftItems.Add(new FileItem { Name = name, FullPath = drive.Name, IsDirectory = true, Size = 0 });
                    }
                    catch { /* skip */ }
                }
            }
            catch
            {
                StatusText = "Ошибка чтения списка дисков.";
            }
            return;
        }
        if (!Directory.Exists(LeftPath))
            return;

        try
        {
            var all = new List<FileItem>();
            foreach (var p in Directory.GetDirectories(LeftPath))
            {
                try { all.Add(new FileItem { Name = Path.GetFileName(p) ?? p, FullPath = p, IsDirectory = true, DateModified = Directory.GetLastWriteTime(p) }); }
                catch { /* skip inaccessible */ }
            }
            foreach (var p in Directory.GetFiles(LeftPath))
            {
                try { var fi = new FileInfo(p); all.Add(new FileItem { Name = fi.Name, FullPath = p, IsDirectory = false, Size = fi.Length, DateModified = fi.LastWriteTime }); }
                catch { /* skip inaccessible */ }
            }
            foreach (var item in ApplySort(all, _leftSortOption))
                LeftItems.Add(item);
        }
        catch
        {
            StatusText = "Ошибка чтения: " + LeftPath;
        }
    }

    private void RefreshRight()
    {
        if (IsRightPanelRemote)
        {
            RightPanelLoading = true;
            StartRequestWithTimeout(RightPath);
            return;
        }
        RightItems.Clear();
        if (string.IsNullOrEmpty(RightPath))
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        var name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.IsNullOrEmpty(name)) name = drive.Name;
                        RightItems.Add(new FileItem { Name = name, FullPath = drive.Name, IsDirectory = true, Size = 0 });
                    }
                    catch { /* skip */ }
                }
            }
            catch { /* ignore */ }
            return;
        }
        if (!Directory.Exists(RightPath))
        {
            RightItems.Add(new FileItem { Name = "(папка пуста или не создана)", FullPath = "", IsDirectory = false });
            return;
        }

        try
        {
            var all = new List<FileItem>();
            foreach (var p in Directory.GetDirectories(RightPath))
            {
                try { all.Add(new FileItem { Name = Path.GetFileName(p) ?? p, FullPath = p, IsDirectory = true }); }
                catch { /* skip inaccessible */ }
            }
            foreach (var p in Directory.GetFiles(RightPath))
            {
                try { all.Add(new FileItem { Name = Path.GetFileName(p) ?? p, FullPath = p, IsDirectory = false, Size = new FileInfo(p).Length }); }
                catch { /* skip inaccessible */ }
            }
            foreach (var item in ApplySort(all, _rightSortOption))
                RightItems.Add(item);
        }
        catch
        {
            RightItems.Add(new FileItem { Name = "(ошибка чтения)", FullPath = "", IsDirectory = false });
        }
        SetDiagnostic("Лев=локально | Прав=локально (папка " + RightPath + ", элементов " + RightItems.Count + ")");
        _mainViewModel?.LogFromFileTransfer("FT right panel filled from local path=" + RightPath + " count=" + RightItems.Count);
    }

    private void ParentLeft()
    {
        if (string.IsNullOrEmpty(LeftPath)) return;
        var parent = Path.GetDirectoryName(LeftPath);
        SetLeftPath(parent ?? "");
    }

    private void HomeLeft()
    {
        SetLeftPath("");
    }

    private void HomeRight()
    {
        SetRightPath("");
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
        }
        catch
        {
            return -1;
        }
    }

    // FormatSize is defined above (line ~533)

    public string GetPropertiesText(IEnumerable<FileItem> items)
    {
        var list = items.Where(x => !string.IsNullOrEmpty(x.FullPath)).ToList();
        if (list.Count == 0) return "Ничего не выбрано.";
        var sb = new System.Text.StringBuilder();
        foreach (var item in list)
        {
            if (sb.Length > 0) sb.AppendLine("———");
            sb.AppendLine("Путь: " + item.FullPath);
            sb.AppendLine("Тип: " + (item.IsDirectory ? "Папка" : "Файл"));
            var size = item.IsDirectory ? GetDirectorySize(item.FullPath) : item.Size;
            sb.AppendLine("Размер: " + FormatSize(size));
        }
        return sb.ToString();
    }

    private void ShowPropertiesLeft()
    {
        var items = SelectedLeftItems.ToList();
        if (items.Count == 0) { StatusText = "Выберите элемент"; return; }
        ShowPropertiesDialogRequested?.Invoke(GetPropertiesText(items));
    }

    private void ShowPropertiesRight()
    {
        var items = SelectedRightItems.ToList();
        if (items.Count == 0) { StatusText = "Выберите элемент"; return; }
        ShowPropertiesDialogRequested?.Invoke(GetPropertiesText(items));
    }

    private void DeleteLeft()
    {
        var items = SelectedLeftItems.Where(x => !string.IsNullOrEmpty(x.FullPath)).ToList();
        if (items.Count == 0) { StatusText = "Выберите элемент для удаления"; return; }
        RequestDeleteConfirmation?.Invoke(items, true);
    }

    private void DeleteRight()
    {
        var items = SelectedRightItems.Where(x => !string.IsNullOrEmpty(x.FullPath)).ToList();
        if (items.Count == 0) { StatusText = "Выберите элемент для удаления"; return; }
        RequestDeleteConfirmation?.Invoke(items, false);
    }

    /// <summary>Выполнить удаление после подтверждения пользователя.</summary>
    public void ConfirmDelete(IEnumerable<FileItem> items, bool isLeft)
    {
        var list = items.ToList();
        if (isLeft)
        {
            // Left panel is always local. Async — большие папки (recursive Delete)
            // блокировали UI thread минутами → Windows выдавала "не отвечает"
            // (bug 2026-04-18, dump confirmed RemoveDirectoryRecursive на dispatcher).
            _ = DeleteLocalItemsAsync(list, isLeft: true);
        }
        else if (IsRightPanelRemote)
        {
            // FT-UX-01 fix: remote panel — only send remote delete requests, never touch local FS.
            foreach (var item in list)
            {
                if (!string.IsNullOrEmpty(item.FullPath))
                    _mainViewModel?.RequestRemoteDelete(item.FullPath);
            }
        }
        else
        {
            // Right panel is local.
            _ = DeleteLocalItemsAsync(list, isLeft: false);
        }
    }

    /// <summary>
    /// Async recursive delete — offload IO в Task.Run чтобы UI не блокировался
    /// на больших папках. Для папки 10k+ файлов sync Directory.Delete занимал
    /// минуты → UI hangs. Сейчас UI thread возвращается сразу, прогресс показывается
    /// через StatusText, по завершении refresh соответствующей панели.
    /// </summary>
    private async Task DeleteLocalItemsAsync(List<FileItem> list, bool isLeft)
    {
        if (list.Count == 0) return;

        StatusText = $"Удаляю {list.Count} элемент(ов)...";
        OnPropertyChanged(nameof(StatusText));

        var errors = 0;
        var success = 0;
        string? lastError = null;

        await Task.Run(() =>
        {
            foreach (var item in list)
            {
                if (string.IsNullOrEmpty(item.FullPath)) continue;
                try
                {
                    if (item.IsDirectory)
                    {
                        if (Directory.Exists(item.FullPath)) Directory.Delete(item.FullPath, recursive: true);
                    }
                    else
                    {
                        if (File.Exists(item.FullPath)) File.Delete(item.FullPath);
                    }
                    success++;
                }
                catch (Exception ex)
                {
                    errors++;
                    lastError = $"{item.Name}: {ex.Message}";
                }
            }
        });

        // Back on UI thread (await resumed).
        if (isLeft) RefreshLeft(); else RefreshRight();

        StatusText = errors == 0
            ? $"Удалено: {success} элемент(ов)"
            : $"Удалено: {success}, ошибок: {errors}. Последняя: {lastError}";
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnDeleteResponseReceived(DeleteResponsePayload payload)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (payload.Success)
            {
                RefreshRight();
                StatusText = "Удалено на удалённой стороне.";
            }
            else
            {
                StatusText = "Ошибка удаления на удалённой стороне: " + (payload.Message ?? "неизвестная ошибка");
            }
        });
    }

    private void OnRenameResponseReceived(RenameResponsePayload payload)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (payload.Success)
            {
                // Auto-refresh remote panel after successful rename.
                if (IsRightPanelRemote)
                    StartRequestWithTimeout(_rightPath);
                else
                    RefreshRight();
                StatusText = "Переименовано.";
            }
            else
            {
                StatusText = "Ошибка переименования: " + (payload.Message ?? "");
            }
        });
    }

    private void ParentRight()
    {
        if (string.IsNullOrEmpty(RightPath)) return;
        if (IsRightPanelRemote)
        {
            var parent = Path.GetDirectoryName(RightPath) ?? "";
            SetRightPath(parent);
            return;
        }
        var parentDir = Path.GetDirectoryName(RightPath) ?? "";
        SetRightPath(parentDir);
    }

    private void SendSelectedFromLeftToRemote()
    {
        var files = SelectedLeftItems.Where(x => !x.IsDirectory && File.Exists(x.FullPath)).ToList();
        var folders = SelectedLeftItems.Where(x => x.IsDirectory && !string.IsNullOrEmpty(x.FullPath) && Directory.Exists(x.FullPath)).ToList();

        foreach (var item in files)
            _fileTransferService.EnqueueSend(item.FullPath);
        foreach (var folder in folders)
            _fileTransferService.EnqueueSendFolder(folder.FullPath);

        var total = files.Count + folders.Count;
        if (total > 0)
            StatusText = $"→ Отправка: {files.Count} файл(ов), {folders.Count} папок";
        // QueueProgress is updated automatically via OnTransferProgress when each file starts.
    }

    private void SendSelectedFromRightToRemote()
    {
        if (IsRightPanelRemote)
        {
            var selected = SelectedRightItems.Where(x => !string.IsNullOrEmpty(x.FullPath)).ToList();
            var folders = selected.Where(x => x.IsDirectory).ToList();
            var files = selected.Where(x => !x.IsDirectory).ToList();
            foreach (var item in files)
                _mainViewModel!.RequestRemoteFile(item.FullPath);
            foreach (var item in folders)
                _mainViewModel!.RequestRemoteFolderDownload(item.FullPath);
            var total = files.Count + folders.Count;
            if (total > 0)
                StatusText = $"← Скачать: {files.Count} файл(ов), {folders.Count} папок. Сохранение в папку приёма.";
            return;
        }
        var toSend = SelectedRightItems.Where(x => !x.IsDirectory && File.Exists(x.FullPath)).ToList();
        foreach (var item in toSend)
            _fileTransferService.EnqueueSend(item.FullPath);
        if (toSend.Count > 0)
            StatusText = $"← Отправка: {toSend.Count} файл(ов) с правой панели (повторная отправка на удалённую сторону).";
    }

    private void OnQueueChanged(int filesAdded, long bytesAdded)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            QueueProgress.ShowSummary = false;
            QueueProgress.EnqueueFiles(filesAdded, bytesAdded);
        });
    }

    private void OnTransferProgress(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var display = GetOrCreateTransferDisplay(item);
            display.Status = item.Status;
            display.CurrentBytes = item.CurrentBytes;
            display.UpdateSpeed();

            // Count INCOMING files here (outgoing already counted via OnQueueChanged).
            if (item.Direction == FileTransfer.TransferDirection.Incoming
                && !_incomingRegistered.Contains(item.TransferId))
            {
                _incomingRegistered.Add(item.TransferId);
                QueueProgress.ShowSummary = false;

                // First file of a batch: use BatchTotal for correct "X из Y" display.
                if (item.BatchTotal > 1 && _incomingRegistered.Count == 1)
                    QueueProgress.EnqueueFiles(item.BatchTotal, item.BatchTotalBytes);
                else if (item.BatchTotal <= 1)
                    QueueProgress.EnqueueFiles(1, item.TotalBytes);
                // else: part of a batch already counted via BatchTotal → skip
            }

            QueueProgress.UpdateCurrentFileProgress(item.FileName, item.CurrentBytes, item.TotalBytes);
            QueueProgress.UpdateSpeed();

            // FT #2 (2026-04-19): отдельный индикатор пока outgoing SCTP уже отправлен
            // (CurrentBytes=TotalBytes), но file_ack от receiver ещё не пришёл
            // (receiver считает hash, двигает из .part в final path). Без этого UI
            // показывал 100% + "Файл 1 из 1" и пользователь думал что готово.
            QueueProgress.IsAwaitingAck = item.Status == TransferStatus.AwaitingAck;
        });
    }

    // Track incoming transferIds to avoid double-counting (outgoing are counted via QueueChanged).
    private readonly HashSet<string> _incomingRegistered = new();

    private void OnTransferCompleted(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var display = GetOrCreateTransferDisplay(item);
            display.Status = TransferStatus.Completed;
            display.CurrentBytes = item.TotalBytes;

            // Aggregated progress
            QueueProgress.FileCompleted(item.TotalBytes > 0 ? item.TotalBytes : item.CurrentBytes);

            // Refresh panels when batch truly finishes (delayed to match _keepActiveUntil).
            var reallyDone = (QueueProgress.CompletedFiles + QueueProgress.FailedFiles) >= QueueProgress.TotalFiles
                             && QueueProgress.TotalFiles > 0;
            if (reallyDone)
            {
                // Delay refresh to after _keepActiveUntil (600ms) expires.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(700);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        RefreshLeft();
                        if (IsRightPanelRemote)
                            StartRequestWithTimeout(_rightPath);
                        else
                            RefreshRight();
                    });
                });
            }

            CleanupOldTransfers();
        });
    }

    private void OnTransferFailed(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var display = GetOrCreateTransferDisplay(item);
            display.Status = item.Status;
            display.ErrorMessage = item.ErrorMessage;

            QueueProgress.FileFailed();
        });
    }

    private TransferDisplayItem GetOrCreateTransferDisplay(TransferItem item)
    {
        var existing = Transfers.FirstOrDefault(t => t.TransferId == item.TransferId);
        if (existing != null) return existing;

        var display = new TransferDisplayItem
        {
            TransferId = item.TransferId,
            FileName = item.FileName,
            SourcePath = item.SourcePath,
            Direction = item.Direction,
            TotalBytes = item.TotalBytes,
            PauseCommand = new RelayCommand(() => _fileTransferService.Pause(item.TransferId)),
            ResumeCommand = new RelayCommand(() => _fileTransferService.Resume(item.TransferId)),
            CancelCommand = new RelayCommand(() => _fileTransferService.Cancel(item.TransferId)),
            RetryCommand = new RelayCommand(() =>
            {
                var path = item.SourcePath;
                if (!string.IsNullOrEmpty(path))
                    _fileTransferService.EnqueueSend(path);
            }),
        };
        display.Status = item.Status;
        display.CurrentBytes = item.CurrentBytes;
        Transfers.Insert(0, display);
        return display;
    }

    private void CleanupOldTransfers()
    {
        // Keep max 20 items, remove oldest completed/failed.
        while (Transfers.Count > 20)
        {
            var oldest = Transfers.LastOrDefault(t => !t.IsActive);
            if (oldest != null)
                Transfers.Remove(oldest);
            else
                break;
        }
    }

    /// <summary>Remove all completed/failed/cancelled transfers from the list.</summary>
    public void ClearCompletedTransfers()
    {
        var toRemove = Transfers.Where(t => !t.IsActive).ToList();
        foreach (var item in toRemove)
            Transfers.Remove(item);
    }

    public void NavigateBreadcrumb(string path, bool isLeft)
    {
        if (isLeft)
            SetLeftPath(path);
        else
            SetRightPath(path);
    }

    private static void UpdateBreadcrumbs(ObservableCollection<BreadcrumbSegment> crumbs, string path)
    {
        crumbs.Clear();
        if (string.IsNullOrEmpty(path) || path == "…") return;

        var normalized = path.Replace('/', '\\');
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = "";
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            accumulated = i == 0 ? part + "\\" : Path.Combine(accumulated, part);
            crumbs.Add(new BreadcrumbSegment
            {
                Label = i == 0 ? part : part,
                FullPath = accumulated,
                IsLast = i == parts.Length - 1
            });
        }
    }

    private static IEnumerable<FileItem> ApplySort(IEnumerable<FileItem> items, SortOption sort)
    {
        return sort.Key switch
        {
            "size" => items.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Size),
            "ext" => items.OrderByDescending(x => x.IsDirectory)
                          .ThenBy(x => Path.GetExtension(x.Name), StringComparer.OrdinalIgnoreCase)
                          .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(x => x.IsDirectory)
                      .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    // UF-05 fix: unsubscribe from all events to prevent leaks.
    public void Dispose()
    {
        _fileTransferService.TransferProgress -= OnTransferProgress;
        _fileTransferService.TransferCompleted -= OnTransferCompleted;
        _fileTransferService.TransferFailed -= OnTransferFailed;
        _fileTransferService.QueueChanged -= OnQueueChanged;
        if (_mainViewModel != null)
        {
            _mainViewModel.RemoteDirListReceived -= OnRemoteDirListReceived;
            _mainViewModel.RequestRemoteDirListFailed -= OnRequestRemoteDirListFailed;
            _mainViewModel.CreateFolderResponseReceived -= OnCreateFolderResponseReceived;
            _mainViewModel.DeleteResponseReceived -= OnDeleteResponseReceived;
            _mainViewModel.RenameResponseReceived -= OnRenameResponseReceived;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
