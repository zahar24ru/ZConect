using System.Windows;

namespace UiApp;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net: ensure background resources are released even if window close handlers were skipped.
        try
        {
            if (Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
            {
                vm.Shutdown();
            }
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }
}
