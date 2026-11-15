using SystemAssistant.App.Services;
using SystemAssistant.Core.Common;

namespace SystemAssistant.App.Mvvm;

/// <summary>所有 ViewModel 的基类：忙碌状态、状态栏文案、多语言联动。</summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private bool _disposed;

    protected ViewModelBase()
    {
        LocalizationService.Instance.LanguageChanged += OnLanguageChangedHandler;
    }

    protected static ServiceHub Services => ServiceHub.Instance;

    protected static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>取本地化文本。</summary>
    protected static string L(string key) => LocalizationService.Instance.Get(key);

    /// <summary>取带参数的本地化文本。</summary>
    protected static string LF(string key, params object[] args) => LocalizationService.Instance.Format(key, args);

    /// <summary>把异步命令的异常写入状态栏，由基类统一构造命令时调用。</summary>
    protected AsyncRelayCommand CreateCommand(Func<Task> execute, Func<bool>? canExecute = null)
        => new(execute, canExecute) { ErrorHandler = ex => StatusMessage = "错误：" + ex.Message };

    protected AsyncRelayCommand CreateCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        => new(execute, canExecute) { ErrorHandler = ex => StatusMessage = "错误：" + ex.Message };

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
        catch (Exception ex) { StatusMessage = "错误：" + ex.Message; }
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
    }
}
