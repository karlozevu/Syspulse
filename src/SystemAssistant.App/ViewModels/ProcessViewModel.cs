using System.Windows.Input;
using Avalonia.Threading;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.App.Views;
using SystemAssistant.Models.Processes;

namespace SystemAssistant.App.ViewModels;

/// <summary>实时进程列表 ViewModel，支持按任意列排序与结束进程 / 进程树。</summary>
public sealed class ProcessViewModel : ViewModelBase
{
    private IReadOnlyList<ProcessSnapshot> _processes = Array.Empty<ProcessSnapshot>();
    private ProcessSnapshot? _selected;
    private string _sortKey = "CpuPercent";
    private bool _sortDescending = true;

    public ProcessViewModel()
    {
        Services.Processes.SnapshotUpdated += OnSnapshot;
        KillCommand = CreateCommand(async p => await KillAsync(p, false));
        KillTreeCommand = CreateCommand(async p => await KillAsync(p, true));
        DetailsCommand = new RelayCommand(p => ShowDetails(p));
        SortCommand = new RelayCommand(p => Sort(p as string ?? "CpuPercent"));
        DashboardCommand = new RelayCommand(() =>
        {
            var window = FloatingDashboardWindow.Current;
            if (window is null) new FloatingDashboardWindow().Show();
            else window.Activate();
        });
    }

    public event Action? ProcessesUpdated;

    public ICommand KillCommand { get; }
    public ICommand KillTreeCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand DashboardCommand { get; }

    public IReadOnlyList<ProcessSnapshot> Processes
    {
        get => _processes;
        private set => Set(ref _processes, value);
    }

    public ProcessSnapshot? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string SortKey => _sortKey;
    public bool SortDescending => _sortDescending;

    private void OnSnapshot(IReadOnlyList<ProcessSnapshot> snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Processes = ApplySort(snapshot);
            ProcessesUpdated?.Invoke();
        });
    }

    /// <summary>表头点击排序：同一列再次点击切换升 / 降序。</summary>
    public void Sort(string key)
    {
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            _sortDescending = key is not "Name" and not "UserName";
        }
        Processes = ApplySort(Processes);
        ProcessesUpdated?.Invoke();
        Raise(nameof(SortKey));
        Raise(nameof(SortDescending));
    }

    private IReadOnlyList<ProcessSnapshot> ApplySort(IEnumerable<ProcessSnapshot> source)
    {
        Func<ProcessSnapshot, object> selector = _sortKey switch
        {
            "ProcessId" => p => p.ProcessId,
            "CpuPercent" => p => p.CpuPercent,
            "WorkingSetBytes" => p => p.WorkingSetBytes,
            "DiskReadBytesPerSecond" => p => p.DiskReadBytesPerSecond,
            "DiskWriteBytesPerSecond" => p => p.DiskWriteBytesPerSecond,
            "UserName" => p => p.UserName,
            _ => p => p.Name
        };
        return (_sortDescending
            ? source.OrderByDescending(selector)
            : source.OrderBy(selector)).ToArray();
    }

    private async Task KillAsync(object? parameter, bool tree)
    {
        var process = parameter as ProcessSnapshot ?? Selected;
        if (process is null) return;
        var action = tree ? L("Process_KillTree") : L("Process_Kill");
        var confirmed = await DialogService.ConfirmAsync(action,
            LF("Process_ConfirmKill", process.Name, process.ProcessId),
            process.ExecutablePath);
        if (!confirmed) return;
        var result = await Services.ProcessService.KillAsync(process.ProcessId, tree);
        StatusMessage = result.Success ? result.Message : result.Message;
        if (!result.Success) await DialogService.ErrorAsync(result.Message);
    }

    private void ShowDetails(object? parameter)
    {
        var process = parameter as ProcessSnapshot ?? Selected;
        if (process is null) return;
        var modules = Services.ProcessService.GetModules(process.ProcessId);
        var handles = Services.ProcessService.GetHandles(process.ProcessId);
        var window = new Views.Dialogs.ProcessDetailWindow(process, modules, handles);
        window.Show(ThemeService.GetMainWindow()!);
    }

    public override void Dispose()
    {
        Services.Processes.SnapshotUpdated -= OnSnapshot;
        base.Dispose();
    }
}
