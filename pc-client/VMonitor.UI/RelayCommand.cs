using System.Windows.Input;

namespace VMonitor.UI;

/// <summary>
/// ICommand の汎用実装。コンストラクタに execute / canExecute デリゲートを渡す。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>CanExecuteChanged を強制的に再評価させる。</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
