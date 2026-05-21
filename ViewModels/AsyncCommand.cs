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
    private static readonly bool AcceptsNullParameter = default(T) is null;

    public AsyncCommand(Func<T, Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) =>
        !_isExecuting && (parameter is T || (parameter is null && AcceptsNullParameter));

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        var typed = parameter is T value ? value : (T)(object?)parameter;
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
