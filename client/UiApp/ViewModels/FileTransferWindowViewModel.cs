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

public sealed class FileTransferWindowViewModel : INotifyPropertyChanged
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
        CreateFolderLeftCommand = new RelayCommand(() => RequestNewFolder?.Invoke(true));
        CreateFolderRightCommand = new RelayCommand(() => RequestNewFolder?.Invoke(false));
        ShowPropertiesLeftCommand = new RelayCommand(ShowPropertiesLeft);
        ShowPropertiesRightCommand = new RelayCommand(ShowPropertiesRight);
        DeleteLeftCommand = new RelayCommand(DeleteLeft);
        DeleteRightCommand = new RelayCommand(DeleteRight);

        _fileTransferService.TransferProgress += OnTransferProgress;
        _fileTransferService.TransferCompleted += OnTransferCompleted;
        _fileTransferService.TransferFailed += OnTransferFailed;

        SetLeftPath("");
        if (_mainViewModel != null && _mainViewModel.IsRemoteFilePanelAvailable)
        {
            _mainViewModel.RemoteDirListReceived += OnRemoteDirListReceived;
            _mainViewModel.RequestRemoteDirListFailed += OnRequestRemoteDirListFailed;
            _mainViewModel.CreateFolderResponseReceived += OnCreateFolderResponseReceived;
            _mainViewModel.DeleteResponseReceived += OnDeleteResponseReceived;
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
    public ObservableCollection<BreadcrumbSegment> LeftBreadcrumbs { get; } = new();
    public ObservableCollection<BreadcrumbSegment> RightBreadcrumbs { get; } = new();

    /// <summary>true = левая панель, false = правая. View показывает диалог и вызывает CreateFolderInLeft(name) или CreateFolderInRight(name).</summary>
    public event Action<bool>? RequestNewFolder;

    /// <summary>Показать диалог свойств (текст с путём и размером).</summary>
    public event Action<string>? ShowPropertiesDialogRequested;

    /// <summary>Запрос подтверждения удаления. View показывает «Точно удалить?» и при «Да» вызывает ConfirmDelete.</summary>
    public event Action<System.Collections.Generic.IEnumerable<FileItem>, bool>? RequestDeleteConfirmation;

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
            if (IsRightPanelRemote && !string.IsNullOrEmpty(value) && value != "…")
                _outgoingTargetDirHolder.Path = value;
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

    public SortOption LeftSortOption
    {
        get => _leftSortOption;
        set { _leftSortOption = value; OnPropertyChanged(); RefreshLeft(); }
    }

    public SortOption RightSortOption
    {
        get => _rightSortOption;
        set { _rightSortOption = value; OnPropertyChanged(); if (!IsRightPanelRemote) RefreshRight(); }
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
            foreach (var r in items)
            {
                RightItems.Add(new FileItem
                {
                    Name = r.Name,
                    FullPath = r.FullPath,
                    IsDirectory = r.IsDirectory,
                    Size = r.Size
                });
            }
            SetDiagnostic("Лев=локально | Прав=удалённо (получено " + items.Count + " элементов, путь " + path + ")");
        });
    }

    public void SelectionLeftChanged(IEnumerable<FileItem> selected)
    {
        SelectedLeftItems.Clear();
        foreach (var item in selected ?? Array.Empty<FileItem>())
            SelectedLeftItems.Add(item);
    }

    public void SelectionRightChanged(IEnumerable<FileItem> selected)
    {
        SelectedRightItems.Clear();
        foreach (var item in selected ?? Array.Empty<FileItem>())
            SelectedRightItems.Add(item);
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
                try { all.Add(new FileItem { Name = Path.GetFileName(p) ?? p, FullPath = p, IsDirectory = true }); }
                catch { /* skip inaccessible */ }
            }
            foreach (var p in Directory.GetFiles(LeftPath))
            {
                try { all.Add(new FileItem { Name = Path.GetFileName(p) ?? p, FullPath = p, IsDirectory = false, Size = new FileInfo(p).Length }); }
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

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024 * 1024)} MB";
        return $"{bytes / (1024L * 1024 * 1024)} GB";
    }

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
        foreach (var item in list)
        {
            if (string.IsNullOrEmpty(item.FullPath)) continue;
            try
            {
                if (item.IsDirectory)
                {
                    if (Directory.Exists(item.FullPath))
                        Directory.Delete(item.FullPath, recursive: true);
                }
                else
                {
                    if (File.Exists(item.FullPath))
                        File.Delete(item.FullPath);
                }
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка удаления " + item.Name + ": " + ex.Message;
            }
        }
        if (isLeft)
            RefreshLeft();
        else if (!IsRightPanelRemote)
            RefreshRight();
        else
        {
            foreach (var item in list)
            {
                if (!string.IsNullOrEmpty(item.FullPath))
                    _mainViewModel?.RequestRemoteDelete(item.FullPath);
            }
        }
        StatusText = list.Count > 0 ? "Удалено: " + list.Count + " элемент(ов)" : "";
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
            StatusText = $"→ Отправка: {files.Count} файл(ов), {folders.Count} папок с левой панели на удалённую сторону.";
    }

    private void SendSelectedFromRightToRemote()
    {
        if (IsRightPanelRemote)
        {
            var toDownload = SelectedRightItems.Where(x => !x.IsDirectory && !string.IsNullOrEmpty(x.FullPath)).ToList();
            foreach (var item in toDownload)
                _mainViewModel!.RequestRemoteFile(item.FullPath);
            if (toDownload.Count > 0)
                StatusText = $"← Скачать с удалённой стороны: {toDownload.Count} файл(ов). Сохранение в папку приёма.";
            return;
        }
        var toSend = SelectedRightItems.Where(x => !x.IsDirectory && File.Exists(x.FullPath)).ToList();
        foreach (var item in toSend)
            _fileTransferService.EnqueueSend(item.FullPath);
        if (toSend.Count > 0)
            StatusText = $"← Отправка: {toSend.Count} файл(ов) с правой панели (повторная отправка на удалённую сторону).";
    }

    private void OnTransferProgress(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var display = GetOrCreateTransferDisplay(item);
            display.Status = item.Status;
            display.CurrentBytes = item.CurrentBytes;
            display.UpdateSpeed();

            // Update legacy single progress bar too.
            var dir = item.Direction == TransferDirection.Outgoing ? "Отправка" : "Приём";
            ProgressText = item.TotalBytes > 0
                ? $"{dir}: {item.FileName} — {item.ProgressPercent}%"
                : $"{dir}: {item.FileName}";
            ProgressValue = Math.Clamp(item.ProgressPercent / 100.0, 0, 1);
            ProgressVisible = true;
        });
    }

    private void OnTransferCompleted(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var display = GetOrCreateTransferDisplay(item);
            display.Status = TransferStatus.Completed;
            display.CurrentBytes = item.TotalBytes;

            // Always refresh both panels: local side may have new files, remote side may have changed too.
            RefreshLeft();
            if (IsRightPanelRemote)
                StartRequestWithTimeout(_rightPath);
            else
                RefreshRight();

            StatusText = item.Direction == TransferDirection.Incoming
                ? "Приём завершён: " + item.FileName
                : "Отправка завершена: " + item.FileName;
            ProgressVisible = false;
            ProgressText = "";
            ProgressValue = 0;

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

            StatusText = "Ошибка передачи: " + (item.ErrorMessage ?? item.FileName);
            ProgressVisible = false;
            ProgressText = "";
            ProgressValue = 0;
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
            Direction = item.Direction,
            TotalBytes = item.TotalBytes,
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

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
