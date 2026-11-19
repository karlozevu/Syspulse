using SystemAssistant.App.Services;
using SystemAssistant.Core.Common;

namespace SystemAssistant.App.Mvvm;

/// <summary>
/// 所有 ViewModel 的基类：忙碌状态、状态栏文案、多语言联动、命令可用性联动、生命周期取消令牌。
/// </summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private bool _disposed;
    private readonly List<AsyncRelayCommand> _commands = new();
    private readonly CancellationTokenSource _lifetime = new();

    protected ViewModelBase()
    {
        LocalizationService.Instance.LanguageChanged += OnLanguageChangedHandler;
        // 任何属性变化都刷新所有异步命令的 CanExecute。
        // 修复：扫描完成 / 选中文件后，依赖 HasResult、ShredFiles.Count 的按钮曾一直保持禁用。
        PropertyChanged += (_, _) => RaiseCommandsCanExecute();
    }

    protected static ServiceHub Services => ServiceHub.Instance;

    protected static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>取本地化文本。</summary>
    protected static string L(string key) => LocalizationService.Instance.Get(key);

    /// <summary>取带参数的本地化文本。</summary>
    protected static string LF(string key, params object[] args) => LocalizationService.Instance.Format(key, args);

    /// <summary>窗口 / ViewModel 关闭时取消，供长时间任务检查。</summary>
    protected CancellationToken LifetimeToken => _lifetime.Token;

    /// <summary>
    /// 把后台线程的回调切回 UI 线程，并在执行时检查 disposed：
    /// 事件回调可能在 Post 入队后、执行前窗口已关闭，此时已投递的 lambda 仍会执行，
    /// 可能在已释放的 ViewModel / 已关闭窗口上操作导致异常。统一在此拦截。
    /// </summary>
    protected void PostUI(Action action)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });

    /// <summary>手动通知所有命令重新计算可用性。</summary>
    protected void RaiseCommandsCanExecute()
    {
        foreach (var command in _commands.ToArray()) command.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 创建同步命令并注入统一异常处理器：保存配置等同步操作若写盘失败，
    /// 错误信息进入状态栏而不是冒泡到 Avalonia 命令框架导致静默失败。
    /// </summary>
    protected RelayCommand CreateSyncCommand(Action execute, Func<bool>? canExecute = null)
        => new(execute, canExecute)
        {
            ErrorHandler = ex => StatusMessage = L("Message_ErrorPrefix") + ex.Message
        };

    /// <summary>把异步命令的异常写入状态栏，由基类统一构造命令时调用。</summary>
    protected AsyncRelayCommand CreateCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(execute, canExecute)
        {
            ErrorHandler = ex => StatusMessage = L("Message_ErrorPrefix") + ex.Message
        };
        _commands.Add(command);
        return command;
    }

    protected AsyncRelayCommand CreateCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(execute, canExecute)
        {
            ErrorHandler = ex => StatusMessage = L("Message_ErrorPrefix") + ex.Message
        };
        _commands.Add(command);
        return command;
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    /// <summary>执行异步任务并维护忙碌状态。</summary>
    protected async Task RunBusyAsync(Func<Task> work, string? startMessage = null)
    {
        if (startMessage is not null) StatusMessage = startMessage;
        IsBusy = true;
        try { await work(); }
        catch (OperationCanceledException) { StatusMessage = L("Status_Cancelled"); }
        catch (Exception ex) { StatusMessage = L("Message_ErrorPrefix") + ex.Message; }
        finally { IsBusy = false; }
    }

    private void OnLanguageChangedHandler(object? sender, EventArgs e)
    {
        OnLanguageChanged();
        RaiseAll();
    }

    /// <summary>子类可重写以刷新自己组合出来的本地化文本。</summary>
    protected virtual void OnLanguageChanged() { }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.Instance.LanguageChanged -= OnLanguageChangedHandler;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        _commands.Clear();
    }
}
