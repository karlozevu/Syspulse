using System.Windows.Input;
using Avalonia.Threading;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Core.Common;
using SystemAssistant.Models.Common;

namespace SystemAssistant.App.ViewModels;

/// <summary>操作日志页：查看、导出 CSV、清空（清空需要确认）。</summary>
public sealed class LogsViewModel : ViewModelBase
{
    private IReadOnlyList<OperationLogEntry> _entries = Array.Empty<OperationLogEntry>();
    private bool _disposed;

    public LogsViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        ExportCommand = CreateCommand(ExportAsync);
        ClearCommand = CreateCommand(ClearAsync);
        if (Services.OperationLog is OperationLogService logService) logService.Changed += OnLogChanged;
        Refresh();
    }

    public event Action? EntriesChanged;

    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ClearCommand { get; }

    public IReadOnlyList<OperationLogEntry> Entries
    {
        get => _entries;
        private set => Set(ref _entries, value);
    }

    public int Count => Entries.Count;

    // OperationLogService.Changed 可能由 Core 的后台任务线程触发，
    // 直接刷新会在线程池线程改写 Avalonia 控件集合；必须 marshal 到 UI 线程。
    private void OnLogChanged()
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() => { if (!_disposed) Refresh(); });
    }

    private void Refresh()
    {
        Entries = Services.OperationLog.GetAll().OrderByDescending(e => e.Timestamp).ToArray();
        Raise(nameof(Count));
        EntriesChanged?.Invoke();
    }

    private async Task ExportAsync()
    {
        var path = await DialogService.SaveFileAsync(L("Logs_Export"),
            $"SystemAssistant_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv", "csv");
        if (string.IsNullOrWhiteSpace(path)) return;
        Services.OperationLog.ExportTo(path);
        StatusMessage = path;
    }

    private async Task ClearAsync()
    {
        var confirmed = await DialogService.ConfirmAsync(L("Logs_Clear"), L("Logs_ConfirmClear"));
        if (!confirmed) return;
        // 清空动作本身也写入一条日志，保证审计链完整
        Services.OperationLog.Clear();
        Services.OperationLog.Log(OperationType.Settings, "清空操作日志", "由用户在日志页面确认执行", true);
        StatusMessage = L("Status_Done");
        Refresh();
    }

    public override void Dispose()
    {
        _disposed = true;
        if (Services.OperationLog is OperationLogService logService) logService.Changed -= OnLogChanged;
        base.Dispose();
    }
}
