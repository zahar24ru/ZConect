using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ZConect.TestRunner;

public partial class MainWindow : Window
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZConect", "TestRunner", "settings.json");

    private static readonly string LogsRoot = @"C:\Soft_pub\ZConect\test_logs";

    /// <summary>Repo root — путь где лежит ZConect.sln / папка client/. GUI полагает что exe
    /// запущен из tools/ZConect.TestRunner/bin/... Через 4 parent hops добираемся до worktree root.</summary>
    private static readonly string RepoRoot = ResolveRepoRoot();

    private Process? _currentProcess;
    private string? _currentLogPath;
    private StreamWriter? _currentLogWriter;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
        Closed += (_, _) =>
        {
            SaveSettings();
            TryKillProcess();
        };
    }

    private static string ResolveRepoRoot()
    {
        // AppContext.BaseDirectory = tools/ZConect.TestRunner/bin/Debug/net8.0-windows/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ZConect.sln")) && !Directory.Exists(Path.Combine(dir.FullName, "client")))
            dir = dir.Parent;
        return dir?.FullName ?? Environment.CurrentDirectory;
    }

    private record Settings(string Login, string Pass, string SaveDir, int TestIndex);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (s is null) return;
            LoginBox.Text = s.Login;
            PassBox.Text = s.Pass;
            SaveDirBox.Text = string.IsNullOrWhiteSpace(s.SaveDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConectReceived")
                : s.SaveDir;
            if (s.TestIndex >= 0 && s.TestIndex < TestPicker.Items.Count)
                TestPicker.SelectedIndex = s.TestIndex;
        }
        catch
        {
            SaveDirBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConectReceived");
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var s = new Settings(LoginBox.Text, PassBox.Text, SaveDirBox.Text, TestPicker.SelectedIndex);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { /* best effort */ }
    }

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите host save directory",
            InitialDirectory = Directory.Exists(SaveDirBox.Text) ? SaveDirBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true)
            SaveDirBox.Text = dlg.FolderName;
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        SaveSettings();

        OutputBox.Clear();
        Directory.CreateDirectory(LogsRoot);
        var tag = ((ComboBoxItem)TestPicker.SelectedItem).Tag?.ToString() ?? "all";
        _currentLogPath = Path.Combine(LogsRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_{tag}.log");
        LogPathBox.Text = _currentLogPath;
        try
        {
            // UTF-8 с BOM чтобы Notepad и другие viewers корректно детектили кодировку.
            _currentLogWriter = new StreamWriter(_currentLogPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        }
        catch { _currentLogWriter = null; }

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "Running...";
        StatusText.Foreground = System.Windows.Media.Brushes.Orange;
        StatusBarText.Text = $"Running {tag}";

        try
        {
            var exitCode = await RunDotnetTestAsync(tag);
            if (exitCode == 0)
            {
                StatusText.Text = "✓ PASSED";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                StatusBarText.Text = $"Passed. Log: {_currentLogPath}";
            }
            else
            {
                StatusText.Text = $"✗ FAILED (exit {exitCode})";
                StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                StatusBarText.Text = $"Failed — см. log: {_currentLogPath}";
            }
        }
        catch (Exception ex)
        {
            AppendLine($"ERROR: {ex.Message}");
            StatusText.Text = "✗ ERROR";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            StatusBarText.Text = "Error — see output";
        }
        finally
        {
            _currentLogWriter?.Dispose();
            _currentLogWriter = null;
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _currentProcess = null;
        }
    }

    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(LoginBox.Text) || LoginBox.Text.Length != 8)
        {
            MessageBox.Show("Введите 8-значный login код с host (Create Session).", "Login", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(PassBox.Text) || PassBox.Text.Length != 8)
        {
            MessageBox.Show("Введите 8-значный pass код с host.", "Pass", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(SaveDirBox.Text))
        {
            MessageBox.Show("Укажите host save dir (по default C:\\Users\\<user>\\Downloads\\ZConectReceived).", "Save dir", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private Task<int> RunDotnetTestAsync(string tag)
    {
        var tcs = new TaskCompletionSource<int>();

        var filter = tag == "LiveFileTransferConflictTests"
            ? "FullyQualifiedName~LiveFileTransferConflictTests"
            : $"FullyQualifiedName~{tag}";

        var testProj = Path.Combine(RepoRoot, "client", "ZConect.Tests", "ZConect.Tests.csproj");
        var args = $"test \"{testProj}\" -c Debug -r win-x64 --nologo --filter \"{filter}\" --logger \"console;verbosity=normal\"";

        var psi = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
            // Force UTF-8 pipe encoding — иначе русский вывод dotnet из Windows console
            // (CP866/CP1251) превращается в кракозябры при UTF-8 decode процессом-родителем.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["ZCONECT_HOST_LOGIN"] = LoginBox.Text.Trim();
        psi.EnvironmentVariables["ZCONECT_HOST_PASS"] = PassBox.Text.Trim();
        psi.EnvironmentVariables["ZCONECT_HOST_SAVE_DIR"] = SaveDirBox.Text.Trim();
        // DOTNET_CLI_UI_LANGUAGE=en — переключает вывод dotnet CLI на английский, чтобы
        // избежать localized string issues. Тесты сами пишут English.
        psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";

        AppendLine($"[GUI] cwd: {RepoRoot}");
        AppendLine($"[GUI] args: dotnet {args}");
        AppendLine($"[GUI] env: ZCONECT_HOST_LOGIN=****{LoginBox.Text.Trim()[^4..]}  ZCONECT_HOST_SAVE_DIR={SaveDirBox.Text.Trim()}");
        AppendLine($"[GUI] log: {_currentLogPath}");
        AppendLine(new string('─', 80));

        _currentProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _currentProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLine(e.Data); };
        _currentProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLine("[stderr] " + e.Data); };
        _currentProcess.Exited += (_, _) => tcs.TrySetResult(_currentProcess?.ExitCode ?? -1);

        _currentProcess.Start();
        _currentProcess.BeginOutputReadLine();
        _currentProcess.BeginErrorReadLine();

        return tcs.Task;
    }

    private void AppendLine(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            OutputBox.AppendText(line + Environment.NewLine);
            OutputBox.ScrollToEnd();
        });
        try { _currentLogWriter?.WriteLine(line); } catch { /* ignore */ }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        TryKillProcess();
        StatusText.Text = "■ Stopped";
        StatusText.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private void TryKillProcess()
    {
        try
        {
            if (_currentProcess is { HasExited: false })
            {
                _currentProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* ignore */ }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentLogPath))
            try { Clipboard.SetText(_currentLogPath); StatusBarText.Text = "Path copied"; } catch { }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(LogsRoot))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentLogPath ?? LogsRoot}\"") { UseShellExecute = true });
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentLogPath) && File.Exists(_currentLogPath))
            Process.Start(new ProcessStartInfo(_currentLogPath) { UseShellExecute = true });
    }
}
