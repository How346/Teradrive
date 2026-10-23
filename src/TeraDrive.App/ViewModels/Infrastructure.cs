using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TeraDrive.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> _exec; readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _exec(p);
}

/// <summary>Runs an async action; prevents re-entry while running and routes exceptions to <see cref="ErrorHandler"/> (never crashes the UI).</summary>
public sealed class AsyncRelayCommand : ICommand
{
    public static Action<Exception>? ErrorHandler;
    readonly Func<object?, Task> _exec; bool _running;
    public AsyncRelayCommand(Func<object?, Task> exec) { _exec = exec; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => !_running;
    public async void Execute(object? p)
    {
        if (_running) return;
        _running = true; CommandManager.InvalidateRequerySuggested();
        try { await _exec(p); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorHandler?.Invoke(ex); }
        finally { _running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
