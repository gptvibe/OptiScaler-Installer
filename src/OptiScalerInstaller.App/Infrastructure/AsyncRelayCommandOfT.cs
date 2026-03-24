using System.Windows.Input;

namespace OptiScalerInstaller.App.Infrastructure;

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T, Task> executeAsync;
    private readonly Predicate<T>? canExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<T, Task> executeAsync, Predicate<T>? canExecute = null)
    {
        this.executeAsync = executeAsync;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !isExecuting &&
           parameter is T typedParameter &&
           (canExecute?.Invoke(typedParameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (parameter is not T typedParameter || !CanExecute(parameter))
        {
            return;
        }

        try
        {
            isExecuting = true;
            NotifyCanExecuteChanged();
            await executeAsync(typedParameter);
        }
        finally
        {
            isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
