using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FileTransfer;
using UiApp.Models;
using UiApp.ViewModels;

namespace UiApp;

public partial class FileTransferWindow : Window
{
    private readonly FileTransferWindowViewModel _vm;
    private DispatcherTimer? _syncSaveDirTimer;

    public FileTransferWindow(FileTransferService fileTransferService, UiApp.Models.IncomingSaveDirHolder incomingSaveDirHolder, UiApp.Models.OutgoingTargetDirHolder outgoingTargetDirHolder, MainViewModel mainViewModel)
    {
        InitializeComponent();
        _vm = new FileTransferWindowViewModel(fileTransferService, incomingSaveDirHolder, outgoingTargetDirHolder, mainViewModel);
        DataContext = _vm;

        // NOTE: OnFileConflict handler теперь wired в MainViewModel.CreateFileTransferService
        // (FT #3 fix 2026-04-19) — работает и когда это окно закрыто, и показывает dialog
        // over FileTransferWindow если оно открыто, иначе over MainWindow.

        _vm.RequestNewFolder += OnRequestNewFolder;
        _vm.RequestInlineEdit += OnRequestInlineEdit;
        _vm.ShowPropertiesDialogRequested += OnShowPropertiesDialogRequested;
        _vm.RequestDeleteConfirmation += OnRequestDeleteConfirmation;
        var svc = new Services.SettingsService();
        var ftSettings = svc.Load();
        MainWindow.RestoreWindowRect(this, ftSettings.FileTransferWindowRect);
        MainWindow.ApplyDarkTitleBar(this, ftSettings.ThemeMode == "Dark");

        Loaded += OnWindowLoaded;
        Activated += (_, _) => _vm.SyncIncomingSaveDirFromLeftPanel();
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _vm.SyncIncomingSaveDirFromLeftPanel();
        _syncSaveDirTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _syncSaveDirTimer.Tick += (_, _) => _vm.SyncIncomingSaveDirFromLeftPanel();
        _syncSaveDirTimer.Start();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            var svc = new Services.SettingsService();
            var s = svc.Load();
            s.FileTransferWindowRect = MainWindow.GetWindowRect(this);
            svc.Save(s);
        }
        catch { /* best effort */ }

        _syncSaveDirTimer?.Stop();
        _syncSaveDirTimer = null;
        _vm.Dispose(); // UF-05 fix: unsubscribe events to prevent leaks
    }

    private void OnRequestNewFolder(bool isLeft)
    {
        var prompt = isLeft ? "Имя новой папки (левая панель):" : "Имя новой папки (правая панель):";
        var name = ShowInputDialog(this, prompt, "Новая папка");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (isLeft)
            _vm.CreateFolderInLeft(name);
        else
            _vm.CreateFolderInRight(name);
    }

    private void OnShowPropertiesDialogRequested(string text)
    {
        var w = new Window
        {
            Style = null,
            Title = "Свойства",
            Width = 380,
            Height = 180,
            MinWidth = 300,
            MinHeight = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize,
            Background = CardBg
        };
        MainWindow.ApplyDarkTitleBar(w, IsDarkTheme);
        var tb = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextPrimary,
            Background = InputBg,
            BorderBrush = InputBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(12),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        w.Content = tb;
        w.ShowDialog();
    }

    private void OnRequestDeleteConfirmation(IEnumerable<FileItem> items, bool isLeft)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var msg = list.Count == 1
            ? "Удалить \"" + list[0].Name + "\"?"
            : "Удалить выбранные элементы (" + list.Count + ")?";
        if (!ShowConfirmDialog(this, msg, "Подтверждение удаления"))
            return;
        _vm.ConfirmDelete(list, isLeft);
    }

    private static bool ShowConfirmDialog(Window owner, string message, string title)
    {
        var w = new Window
        {
            Style = null, // disable WPF-UI implicit Window style that blocks SizeToContent
            Title = title,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            Background = CardBg
        };
        MainWindow.ApplyDarkTitleBar(w, IsDarkTheme);
        var stack = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = TextPrimary,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var noBtn = MakeDialogButton("Отмена", false);
        noBtn.IsCancel = true;
        noBtn.Margin = new Thickness(0, 0, 8, 0);
        var deleteBtn = new Button
        {
            Content = "Удалить",
            Width = 86,
            Height = 28,
            FontSize = 12,
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        // Red delete button — use theme DangerBrush/DangerHoverBrush
        var redBg = (System.Windows.Media.Brush?)Application.Current?.Resources["DangerBgBrush"]
                    ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        var redHover = (System.Windows.Media.Brush?)Application.Current?.Resources["DangerHoverBrush"]
                       ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));
        var redTemplate = new ControlTemplate(typeof(Button));
        var redBorder = new FrameworkElementFactory(typeof(Border));
        redBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        redBorder.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        redBorder.SetValue(Border.BackgroundProperty, redBg);
        redBorder.Name = "Bd";
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.White);
        redBorder.AppendChild(cp);
        redTemplate.VisualTree = redBorder;
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, redHover, "Bd"));
        redTemplate.Triggers.Add(hoverTrigger);
        deleteBtn.Template = redTemplate;

        bool confirmed = false;
        deleteBtn.Click += (_, _) => { confirmed = true; w.DialogResult = true; w.Close(); };
        noBtn.Click += (_, _) => w.Close();
        panel.Children.Add(noBtn);
        panel.Children.Add(deleteBtn);
        stack.Children.Add(panel);
        w.Content = stack;
        w.ShowDialog();
        return confirmed;
    }

    private FileTransfer.ConflictAction ShowFileConflictDialog(string existingPath)
        => ShowFileConflictDialogStatic(existingPath, this);

    /// <summary>Show file-conflict dialog. Owner may be null (dialog will center on screen).
    /// Called from MainViewModel fallback когда FileTransferWindow закрыт (FT #3 fix 2026-04-19):
    /// раньше при закрытом FT окне OnFileConflict был null → silent rename. Теперь диалог
    /// показывается всегда — над FT window если она открыта, иначе над MainWindow.</summary>
    public static FileTransfer.ConflictAction ShowFileConflictDialogStatic(string existingPath, Window? owner)
    {
        var fileName = System.IO.Path.GetFileName(existingPath);
        var w = new Window
        {
            Style = null,
            Title = "Файл уже существует",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            Background = CardBg
        };
        MainWindow.ApplyDarkTitleBar(w, IsDarkTheme);
        var stack = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = $"Файл \"{fileName}\" уже существует в папке назначения.",
            FontSize = 12, Foreground = TextPrimary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });

        var result = FileTransfer.ConflictAction.Rename;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var skipBtn = MakeDialogButton("Пропустить", false);
        skipBtn.Margin = new Thickness(0, 0, 6, 0);
        skipBtn.Click += (_, _) => { result = FileTransfer.ConflictAction.Skip; w.DialogResult = true; w.Close(); };

        var renameBtn = MakeDialogButton("Переименовать", false);
        renameBtn.Margin = new Thickness(0, 0, 6, 0);
        renameBtn.Click += (_, _) => { result = FileTransfer.ConflictAction.Rename; w.DialogResult = true; w.Close(); };

        var overwriteBtn = MakeDialogButton("Заменить", true);
        overwriteBtn.Margin = new Thickness(0, 0, 6, 0);
        overwriteBtn.Click += (_, _) => { result = FileTransfer.ConflictAction.Overwrite; w.DialogResult = true; w.Close(); };

        panel.Children.Add(skipBtn);
        panel.Children.Add(renameBtn);
        panel.Children.Add(overwriteBtn);
        stack.Children.Add(panel);

        // "Apply to all" checkbox
        var applyAll = new System.Windows.Controls.CheckBox
        {
            Content = "Применить ко всем", FontSize = 11, Foreground = TextSecondary,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(applyAll);

        w.Content = stack;
        w.ShowDialog();

        if (applyAll.IsChecked == true)
        {
            return result switch
            {
                FileTransfer.ConflictAction.Skip => FileTransfer.ConflictAction.SkipAll,
                FileTransfer.ConflictAction.Overwrite => FileTransfer.ConflictAction.OverwriteAll,
                _ => result
            };
        }
        return result;
    }

    // Theme-aware brush lookups — resolved at call time from Application.Resources,
    // so dialogs pick up current theme (Light/Dark) rather than using frozen defaults.
    private static System.Windows.Media.Brush DialogBg
        => (System.Windows.Media.Brush?)Application.Current?.Resources["WindowBgBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF7, 0xFC));
    private static System.Windows.Media.Brush CardBg
        => (System.Windows.Media.Brush?)Application.Current?.Resources["SurfaceBgBrush"]
           ?? System.Windows.Media.Brushes.White;
    private static System.Windows.Media.Brush CardBorder
        => (System.Windows.Media.Brush?)Application.Current?.Resources["BorderBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static System.Windows.Media.Brush AccentBg
        => (System.Windows.Media.Brush?)Application.Current?.Resources["PrimaryBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB));
    private static System.Windows.Media.Brush AccentHover
        => (System.Windows.Media.Brush?)Application.Current?.Resources["PrimaryHoverBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0xD8));
    private static System.Windows.Media.Brush InputBg
        => (System.Windows.Media.Brush?)Application.Current?.Resources["SurfaceAltBgBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static System.Windows.Media.Brush InputBorder
        => (System.Windows.Media.Brush?)Application.Current?.Resources["SeparatorBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xED, 0xF2, 0xF7));
    private static System.Windows.Media.Brush TextPrimary
        => (System.Windows.Media.Brush?)Application.Current?.Resources["TextPrimaryBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A));
    private static System.Windows.Media.Brush TextSecondary
        => (System.Windows.Media.Brush?)Application.Current?.Resources["TextSecondaryBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
    private static System.Windows.Media.Brush HoverBg
        => (System.Windows.Media.Brush?)Application.Current?.Resources["HoverBgBrush"]
           ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9));

    private static bool IsDarkTheme => new Services.SettingsService().Load().ThemeMode == "Dark";

    private static Button MakeDialogButton(string text, bool isPrimary)
    {
        var btn = new Button
        {
            Content = text,
            Height = 30,
            FontSize = 12,
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(isPrimary ? 0 : 1),
        };
        btn.Template = CreateButtonTemplate(isPrimary);
        return btn;
    }

    private static ControlTemplate CreateButtonTemplate(bool isPrimary)
    {
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetValue(Border.PaddingProperty, new System.Windows.TemplateBindingExtension(Button.PaddingProperty));
        borderFactory.Name = "Bd";

        if (isPrimary)
        {
            borderFactory.SetValue(Border.BackgroundProperty, AccentBg);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.White);
            borderFactory.AppendChild(cp);
        }
        else
        {
            borderFactory.SetValue(Border.BackgroundProperty, CardBg);
            borderFactory.SetValue(Border.BorderBrushProperty, CardBorder);
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(TextBlock.ForegroundProperty, TextPrimary);
            borderFactory.AppendChild(cp);
        }

        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, isPrimary ? AccentHover : HoverBg, "Bd"));
        template.Triggers.Add(hoverTrigger);

        return template;
    }

    private static string? ShowInputDialog(Window owner, string prompt, string title)
    {
        var w = new Window
        {
            Style = null,
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            Background = CardBg
        };
        MainWindow.ApplyDarkTitleBar(w, IsDarkTheme);
        var stack = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = prompt,
            FontSize = 13,
            Foreground = TextPrimary,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });
        var tb = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 16),
            MinHeight = 32,
            FontSize = 13,
            Padding = new Thickness(10, 6, 10, 6),
            Background = InputBg,
            BorderBrush = InputBorder,
            BorderThickness = new Thickness(1),
            Foreground = TextPrimary
        };
        stack.Children.Add(tb);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = MakeDialogButton("Создать", true);
        ok.IsDefault = true;
        ok.Margin = new Thickness(0, 0, 8, 0);
        var cancel = MakeDialogButton("Отмена", false);
        cancel.IsCancel = true;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(cancel);
        panel.Children.Add(ok);
        stack.Children.Add(panel);
        w.Content = stack;
        w.Loaded += (_, _) => tb.Focus();
        w.ShowDialog();
        return result;
    }

    private void LeftBreadcrumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBlock tb && tb.Tag is string path)
            _vm.NavigateBreadcrumb(path, isLeft: true);
    }

    private void RightBreadcrumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBlock tb && tb.Tag is string path)
            _vm.NavigateBreadcrumb(path, isLeft: false);
    }

    private void LeftList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FileTransferWindowViewModel vm || sender is not ListBox list)
            return;
        var selected = list.SelectedItems.Cast<Models.FileItem>().ToList();
        vm.SelectionLeftChanged(selected);
    }

    private void LeftList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsClickOnScrollBar(e)) return;
        if (DataContext is not FileTransferWindowViewModel vm || sender is not ListBox list)
            return;
        if (list.SelectedItem is not Models.FileItem item)
            return;
        if (item.IsDirectory)
            vm.NavigateLeft(item);
    }

    private void RightList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FileTransferWindowViewModel vm || sender is not ListBox list)
            return;
        var selected = list.SelectedItems.Cast<Models.FileItem>().ToList();
        vm.SelectionRightChanged(selected);
    }

    private void RightList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsClickOnScrollBar(e)) return;
        if (DataContext is not FileTransferWindowViewModel vm || sender is not ListBox list)
            return;
        if (list.SelectedItem is not Models.FileItem item)
            return;
        if (item.IsDirectory)
            vm.NavigateRight(item);
    }

    /// <summary>Check if the mouse event originated from a ScrollBar.</summary>
    private static bool IsClickOnScrollBar(MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null)
        {
            if (hit is System.Windows.Controls.Primitives.ScrollBar) return true;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return false;
    }

    private void LeftList_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        HandleListKeyDown(list, e, isLeft: true);
    }

    private void RightList_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        HandleListKeyDown(list, e, isLeft: false);
    }

    private void HandleListKeyDown(ListBox list, KeyEventArgs e, bool isLeft)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.A && mods == ModifierKeys.Control)
        {
            list.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            var cmd = isLeft ? _vm.DeleteLeftCommand : _vm.DeleteRightCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (list.SelectedItem is Models.FileItem item && item.IsDirectory)
            {
                if (isLeft) _vm.NavigateLeft(item); else _vm.NavigateRight(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            var cmd = isLeft ? _vm.ParentLeftCommand : _vm.ParentRightCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            (isLeft ? _vm.RenameLeftCommand : _vm.RenameRightCommand).Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            (isLeft ? _vm.RefreshLeftCommand : _vm.RefreshRightCommand).Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.C && mods == ModifierKeys.Control)
        {
            (isLeft ? _vm.CopyPathLeftCommand : _vm.CopyPathRightCommand).Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.N && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            (isLeft ? _vm.CreateFolderLeftCommand : _vm.CreateFolderRightCommand).Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            list.UnselectAll();
            e.Handled = true;
        }
    }

    // ── View Mode ──────────────────────────────────────────────────────

    private void ViewMode_Details(object sender, RoutedEventArgs e) => SetViewMode("Details");
    private void ViewMode_Small(object sender, RoutedEventArgs e) => SetViewMode("SmallIcons");
    private void ViewMode_Large(object sender, RoutedEventArgs e) => SetViewMode("LargeIcons");

    private void SetViewMode(string mode)
    {
        _vm.ViewMode = mode;
        var template = mode switch
        {
            "SmallIcons" => (DataTemplate)Resources["FileItemSmallTemplate"],
            "LargeIcons" => (DataTemplate)Resources["FileItemLargeTemplate"],
            _ => (DataTemplate)Resources["FileItemDetailsTemplate"]
        };
        LeftList.ItemTemplate = template;
        RightList.ItemTemplate = template;

        // For large icons, use WrapPanel; for others, use default StackPanel
        if (mode == "LargeIcons")
        {
            LeftList.ItemsPanel = new ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.WrapPanel)));
            RightList.ItemsPanel = new ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.WrapPanel)));
        }
        else
        {
            LeftList.ItemsPanel = new ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.VirtualizingStackPanel)));
            RightList.ItemsPanel = new ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.VirtualizingStackPanel)));
        }
    }

    // ── Inline Edit (Rename / New Folder) ───────────────────────────────

    private void OnRequestInlineEdit(bool isLeft, Models.FileItem item, Action<string> callback)
    {
        var list = isLeft ? LeftList : RightList;

        // Find the ListBoxItem container for this item
        list.UpdateLayout();
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListBoxItem;
        if (container is null)
        {
            // Fallback — scroll to item and retry
            list.ScrollIntoView(item);
            list.UpdateLayout();
            container = list.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListBoxItem;
        }
        if (container is null) return;

        // Create a TextBox overlay positioned over the item name
        var tb = new System.Windows.Controls.TextBox
        {
            Text = item.Name,
            FontSize = 13,
            Padding = new Thickness(2, 1, 2, 1),
            MinWidth = 150,
            SelectionStart = 0,
            SelectionLength = System.IO.Path.GetFileNameWithoutExtension(item.Name).Length, // select name without extension
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1.5),
        };

        // Replace the content temporarily
        var originalContent = container.Content;
        var adornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(container);

        // Simpler approach: put TextBox in a Popup positioned at the container
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = container,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
            StaysOpen = false,
            Child = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                Child = tb
            },
            IsOpen = true
        };
        tb.BorderThickness = new Thickness(0);

        bool committed = false;
        void Commit()
        {
            if (committed) return;
            committed = true;
            popup.IsOpen = false;
            var newName = tb.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(newName) && newName != item.Name)
                callback(newName);
            else
                callback(""); // cancel
        }

        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { committed = true; popup.IsOpen = false; callback(""); e.Handled = true; }
        };
        tb.LostFocus += (_, _) => Commit();
        popup.Closed += (_, _) => { if (!committed) callback(""); };

        Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Drag & Drop ───────────────────────────────────────────────────────

    // ── Rubber Band Selection ──────────────────────────────────────────

    private void LeftList_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Start rubber band only if clicking on empty space (not on an item)
        if (e.OriginalSource is System.Windows.Controls.ScrollViewer or System.Windows.Controls.Grid or System.Windows.Controls.Border)
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
                LeftList.UnselectAll();
        }
    }

    private void RightList_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.ScrollViewer or System.Windows.Controls.Grid or System.Windows.Controls.Border)
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
                RightList.UnselectAll();
        }
    }

    // ── Drag & Drop between panels ──────────────────────────────────────

    private bool _isDragging;

    private void List_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("FTPanel")
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void LeftList_OnDrop(object sender, DragEventArgs e)
    {
        // Drop from right panel → download/copy to left (local)
        if (e.Data.GetDataPresent("FTPanel") && e.Data.GetData("FTPanel") is string source && source == "Right")
        {
            _vm.SendFromRightToRemoteCommand.Execute(null); // triggers download from remote
            return;
        }
        // Drop from Explorer
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var path in paths)
        {
            if (System.IO.File.Exists(path))
                _vm.EnqueueSendExternal(path);
            else if (System.IO.Directory.Exists(path))
                _vm.EnqueueSendFolderExternal(path);
        }
    }

    private void RightList_OnDrop(object sender, DragEventArgs e)
    {
        // Drop from left panel → upload to right (remote)
        if (e.Data.GetDataPresent("FTPanel") && e.Data.GetData("FTPanel") is string source && source == "Left")
        {
            _vm.SendToRemoteCommand.Execute(null); // triggers upload to remote
            return;
        }
        // Drop from Explorer
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var path in paths)
        {
            if (System.IO.File.Exists(path))
                _vm.EnqueueSendExternal(path);
            else if (System.IO.Directory.Exists(path))
                _vm.EnqueueSendFolderExternal(path);
        }
    }

    private void LeftList_OnMouseMove(object sender, MouseEventArgs e)
    {
        StartDragIfNeeded(sender, e, "Left");
    }

    private void RightList_OnMouseMove(object sender, MouseEventArgs e)
    {
        StartDragIfNeeded(sender, e, "Right");
    }

    private void StartDragIfNeeded(object sender, MouseEventArgs e, string panelId)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;
        var list = sender as ListBox;
        if (list is null || list.SelectedItems.Count == 0) return;

        // Don't start drag from scrollbar or empty area — only from ListBoxItem.
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit != list)
        {
            if (hit is System.Windows.Controls.Primitives.ScrollBar) return;
            if (hit is System.Windows.Controls.ListBoxItem) break;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        if (hit is not System.Windows.Controls.ListBoxItem) return;

        _isDragging = true;
        try
        {
            var data = new DataObject();
            data.SetData("FTPanel", panelId);
            DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
        }
        finally { _isDragging = false; }
    }
}
