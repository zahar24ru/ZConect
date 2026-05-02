using System.Windows.Input;

namespace UiApp.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;

    public RelayCommand(Action<T> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        if (parameter is T t) _execute(t);
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isBusy;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isBusy;

    public async void Execute(object? parameter)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // UF-01 fix: log instead of swallowing exceptions from async commands.
            System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand] unhandled: {ex}");
        }
        finally
        {
            _isBusy = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private bool _isBusy;

    public AsyncRelayCommand(Func<T?, Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isBusy;

    public async void Execute(object? parameter)
    {
        if (_isBusy) return;
        _isBusy = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var typed = parameter is T t ? t : default;
            await _execute(typed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand<T>] unhandled: {ex}");
        }
        finally
        {
            _isBusy = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
