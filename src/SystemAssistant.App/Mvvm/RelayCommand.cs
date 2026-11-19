using System.Windows.Input;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.Mvvm;

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// 未注入 ErrorHandler 时的兜底：写诊断日志而不是重新抛出。
    /// 之前 `else throw` 会把同步命令异常抛给 Avalonia 命令框架，轻则无中文提示、
    /// 重则触发 UnhandledException 导致整个应用退出。控件层（UiFactory/TableView）
    /// 的命令没有 ViewModel 上下文，无法写状态栏，但绝不能因此崩溃应用。
    /// </summary>
    private static void LogUnhandled(Exception ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                AppPaths.DiagnosticsLogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [RelayCommand] {ex}{Environment.NewLine}");
        }
        catch { /* 诊断日志写失败不能再次抛异常 */ }
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>统一异常处理入口（可选），与 AsyncRelayCommand 行为对齐。</summary>
    public Action<Exception>? ErrorHandler { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        // 同步命令此前完全没有异常保护：保存设置时磁盘满 / 文件占用 / NaN 序列化
        // 等异常会冒泡到 Avalonia 命令框架，界面无提示且状态栏丢失。这里统一兜底。
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            if (ErrorHandler is not null) ErrorHandler(ex);
            else LogUnhandled(ex);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令：执行期间自动禁用，异常统一交给 ErrorHandler，避免 async void 未捕获异常导致崩溃。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>统一异常处理入口，由 ViewModelBase 注入。</summary>
    public Action<Exception>? ErrorHandler { get; set; }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消，不弹错误
        }
        catch (Exception ex)
        {
            ErrorHandler?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
