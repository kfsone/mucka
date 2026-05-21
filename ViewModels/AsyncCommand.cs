using System.Windows.Input;

namespace Mucka.ViewModels;

/// <summary>ICommand wrapper for async operations; disables itself while executing.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? _) => !_isExecuting;

    public async void Execute(object? _)
    {
        if (_isExecuting)
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>ICommand wrapper for async operations with a typed parameter; disables itself while executing.</summary>
public sealed class AsyncCommand<T> : ICommand
{
    private readonly Func<T, Task> _execute;
    private bool _isExecuting;

    public AsyncCommand(Func<T, Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? _) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        if (_isExecuting || parameter is not T typed)
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(typed);
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
