using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.ViewModels;

internal abstract class ViewModelBase : ObservableObject;

internal sealed class AsyncCommand(
    Func<Task> execute,
    Action<Exception> onException,
    Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            AppLogService.RecordCommandFailure(exception);
            onException(exception);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
