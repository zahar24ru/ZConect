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
        _vm.RequestNewFolder += OnRequestNewFolder;
        _vm.ShowPropertiesDialogRequested += OnShowPropertiesDialogRequested;
        _vm.RequestDeleteConfirmation += OnRequestDeleteConfirmation;
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
        _syncSaveDirTimer?.Stop();
        _syncSaveDirTimer = null;
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
            Title = "Свойства",
            Width = 340,
            Height = 200,
            MinWidth = 280,
            MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize
        };
        var tb = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10),
            Padding = new Thickness(6),
            FontSize = 12,
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
            ? "Точно желаете удалить \"" + list[0].Name + "\"?"
            : "Точно желаете удалить выбранные элементы (" + list.Count + ")?";
        if (MessageBox.Show(this, msg, "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _vm.ConfirmDelete(list, isLeft);
    }

    private static string? ShowInputDialog(Window owner, string prompt, string title)
    {
        var w = new Window
        {
            Title = title,
            Width = 360,
            MinHeight = 160,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner
        };
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        var tb = new TextBox { Margin = new Thickness(0, 0, 0, 14), MinHeight = 24 };
        stack.Children.Add(tb);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Width = 80, Height = 26 };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        stack.Children.Add(panel);
        w.Content = stack;
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
        if (DataContext is not FileTransferWindowViewModel vm || sender is not ListBox list)
            return;
        if (list.SelectedItem is not Models.FileItem item)
            return;
        if (item.IsDirectory)
            vm.NavigateRight(item);
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
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            list.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            var cmd = isLeft ? _vm.DeleteLeftCommand : _vm.DeleteRightCommand;
            if (cmd.CanExecute(null))
                cmd.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (list.SelectedItem is Models.FileItem item && item.IsDirectory)
            {
                if (isLeft) _vm.NavigateLeft(item);
                else _vm.NavigateRight(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            var cmd = isLeft ? _vm.ParentLeftCommand : _vm.ParentRightCommand;
            if (cmd.CanExecute(null))
                cmd.Execute(null);
            e.Handled = true;
        }
    }
}
