using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using UiApp.ViewModels;

namespace UiApp;

public partial class RemoteScreenWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private bool _allowRealClose;

    public RemoteScreenWindow()
    {
        InitializeComponent();
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

        // UX: closing the window should not disconnect the session.
        // Hide instead; user can reopen via "Подключиться" (Viewer) which re-activates this window.
        e.Cancel = true;
        try
        {
            Hide();
            Vm?.HandleRemoteSurfaceLostFocus();
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

