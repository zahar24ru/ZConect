using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using UiApp.ViewModels;
using Wpf.Ui.Appearance;

namespace UiApp;

public partial class RemoteScreenWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private bool _allowRealClose;

    public RemoteScreenWindow()
    {
        InitializeComponent();

        // RemoteScreenWindow is always dark — no per-window theme needed,
        // dark ComboBox styles handled via local Window.Resources in XAML.

        Closing += OnClosing;
    }

    public void AllowRealClose()
    {
        _allowRealClose = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowRealClose)
        {
            return;
        }

        // Закрытие окна удалённого экрана = завершение подключения viewer.
        e.Cancel = true;
        try
        {
            Hide();
            Vm?.HandleRemoteSurfaceLostFocus();
            // Инициируем отключение viewer-сессии.
            Vm?.DisconnectViewerAsync();
        }
        catch
        {
            // ignore
        }
    }

    private void RemoteScreenSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        Vm?.HandleRemoteSurfaceMouseMove((FrameworkElement)sender, e);
    }

    private void RemoteScreenSurface_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        ((FrameworkElement)sender).Focus();
        Vm?.HandleRemoteSurfaceMouseDown((FrameworkElement)sender, e);
    }

    private void RemoteScreenSurface_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        Vm?.HandleRemoteSurfaceMouseUp((FrameworkElement)sender, e);
    }

    private void RemoteScreenSurface_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Vm?.HandleRemoteSurfaceMouseWheel((FrameworkElement)sender, e);
    }

    private void RemoteScreenSurface_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+I — toggle quality overlay (FPS/bitrate/RTT). Intercept locally;
        // do not forward to host (keeps hotkey local-only, like F11/esc etc.)
        if (e.Key == Key.I
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (Vm is not null) Vm.ShowQualityOverlay = !Vm.ShowQualityOverlay;
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+I — backward-compat alias для extended ICE route info.
        if (e.Key == Key.I
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (Vm is not null) Vm.ShowIceDetailsOverlay = !Vm.ShowIceDetailsOverlay;
            e.Handled = true;
            return;
        }
        Vm?.HandleRemoteSurfaceKeyDown(e);
    }

    private void RemoteScreenSurface_OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        Vm?.HandleRemoteSurfaceKeyUp(e);
    }

    private void RemoteScreenSurface_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Vm?.HandleRemoteSurfaceLostFocus();
    }
}

