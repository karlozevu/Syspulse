using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.ViewModels;

/// <summary>整机概览页 ViewModel：一次性读取全部硬件信息并支持三种格式导出。</summary>
public sealed class OverviewViewModel : ViewModelBase
{
    private SystemOverview? _overview;
    private string _osName = string.Empty;
    private string _osVersion = string.Empty;
    private string _build = string.Empty;
    private string _architecture = string.Empty;
    private string _installDate = string.Empty;
    private string _bootTime = string.Empty;
    private string _computerName = string.Empty;
    private string _userName = string.Empty;
    private string _mainboardVendor = string.Empty;
    private string _mainboardModel = string.Empty;
    private string _biosVendor = string.Empty;
    private string _biosVersion = string.Empty;
    private string _biosDate = string.Empty;
    private string _cpuSummary = string.Empty;
    private string _memorySummary = string.Empty;
    private string _gpuSummary = string.Empty;
    private string _diskSummary = string.Empty;
    private string _networkSummary = string.Empty;
    private string _batterySummary = string.Empty;
    private string _displaySummary = string.Empty;
    private string _generatedAt = string.Empty;

    public OverviewViewModel()
    {
        RefreshCommand = CreateCommand(LoadAsync);
        ExportCommand = CreateCommand(async p => await ExportAsync(p as string ?? "txt"));
        _ = LoadAsync();
    }

    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }

    public string OsName { get => _osName; private set => Set(ref _osName, value); }
    public string OsVersion { get => _osVersion; private set => Set(ref _osVersion, value); }
    public string Build { get => _build; private set => Set(ref _build, value); }
    public string Architecture { get => _architecture; private set => Set(ref _architecture, value); }
    public string InstallDate { get => _installDate; private set => Set(ref _installDate, value); }
    public string BootTime { get => _bootTime; private set => Set(ref _bootTime, value); }
    public string ComputerName { get => _computerName; private set => Set(ref _computerName, value); }
    public string UserName { get => _userName; private set => Set(ref _userName, value); }
    public string MainboardVendor { get => _mainboardVendor; private set => Set(ref _mainboardVendor, value); }
    public string MainboardModel { get => _mainboardModel; private set => Set(ref _mainboardModel, value); }
    public string BiosVendor { get => _biosVendor; private set => Set(ref _biosVendor, value); }
    public string BiosVersion { get => _biosVersion; private set => Set(ref _biosVersion, value); }
    public string BiosDate { get => _biosDate; private set => Set(ref _biosDate, value); }
    public string CpuSummary { get => _cpuSummary; private set => Set(ref _cpuSummary, value); }
    public string MemorySummary { get => _memorySummary; private set => Set(ref _memorySummary, value); }
    public string GpuSummary { get => _gpuSummary; private set => Set(ref _gpuSummary, value); }
    public string DiskSummary { get => _diskSummary; private set => Set(ref _diskSummary, value); }
    public string NetworkSummary { get => _networkSummary; private set => Set(ref _networkSummary, value); }
    public string BatterySummary { get => _batterySummary; private set => Set(ref _batterySummary, value); }
    public string DisplaySummary { get => _displaySummary; private set => Set(ref _displaySummary, value); }
    public string GeneratedAt { get => _generatedAt; private set => Set(ref _generatedAt, value); }

    private async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            StatusMessage = L("Common_Loading");
            var overview = await Services.Hardware.GetOverviewAsync();
            Apply(overview);
            StatusMessage = L("Status_Done");
        });
    }

    private void Apply(SystemOverview o)
    {
        _overview = o;
        OsName = o.OperatingSystem.Caption;
        OsVersion = o.OperatingSystem.Version;
        Build = o.OperatingSystem.BuildNumber;
        Architecture = o.OperatingSystem.Architecture;
        InstallDate = o.OperatingSystem.InstallDate;
        BootTime = o.OperatingSystem.LastBootTime;
        ComputerName = o.OperatingSystem.ComputerName;
        UserName = o.OperatingSystem.UserName;
        MainboardVendor = o.Motherboard.Manufacturer;
        MainboardModel = o.Motherboard.Model;
        BiosVendor = o.Bios.Manufacturer;
        BiosVersion = o.Bios.Version;
        BiosDate = o.Bios.ReleaseDate;
        GeneratedAt = o.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss");

        CpuSummary = $"{o.Cpu.Name} · {o.Cpu.PhysicalCores} {L("Label_Cores")} / {o.Cpu.LogicalProcessors} {L("Label_Threads")} · {o.Cpu.BaseClockMHz:F0} MHz";
        MemorySummary = $"{FormatHelper.FormatBytes(o.Memory.TotalBytes)} · {L("Label_Used")} {FormatHelper.FormatBytes(o.Memory.UsedBytes)} ({o.Memory.UsagePercent:F1} %)";
        GpuSummary = o.Gpus.Count == 0
            ? L("Common_None")
            : string.Join("；", o.Gpus.Select(g =>
                $"{g.Name}（{(g.VramTotalBytes > 0 ? FormatHelper.FormatBytes(g.VramTotalBytes) : L("Common_NA"))}）"));
        DiskSummary = o.Disks.Count == 0
            ? L("Common_None")
            : string.Join("；", o.Disks.Select(d =>
                $"{L("Label_Disk")} {d.DiskIndex} {d.Model} {FormatHelper.FormatBytes(d.TotalBytes)} [{d.MediaTypeText} / {HealthText(d.Health)}]"));
        NetworkSummary = o.NetworkAdapters.Count == 0
            ? L("Common_None")
            : string.Join("；", o.NetworkAdapters.Select(n => $"{n.Name} {n.IpAddresses}"));
        BatterySummary = o.Battery.IsPresent
            ? $"{o.Battery.FullChargeCapacityMwh} / {o.Battery.DesignCapacityMwh} mWh · {L("Label_Wear")} {o.Battery.WearPercent:F1} % · {L("Label_Charge")} {o.Battery.ChargePercent:F1} %"
            : L("Hardware_BatteryNone");
        DisplaySummary = o.Displays.Count == 0
            ? L("Common_None")
            : string.Join("；", o.Displays.Select(d => $"{d.DeviceName} {d.ResolutionText} {(d.HdrEnabled == true ? "HDR" : "")}"));

        RaiseAll();
    }

    private async Task ExportAsync(string format)
    {
        if (_overview is null) await LoadAsync();
        if (_overview is null) return;
        var directory = Services.Settings.Current.DefaultExportDirectory;
        if (string.IsNullOrWhiteSpace(directory)) directory = AppPaths.DefaultExportDirectory;
        var path = Services.Reports.Export(_overview, directory, format);
        Services.OperationLog.Log(Models.Common.OperationType.File, "导出整机报告", path, true);
        StatusMessage = path;
        await DialogService.MessageAsync(L("Common_Export"), path);
    }

    // 组合文本是缓存字符串，语言切换时必须用同一份数据重新生成。
    protected override void OnLanguageChanged()
    {
        if (_overview is not null) Apply(_overview);
    }

    private static string HealthText(DiskHealthStatus status) => status switch
    {
        DiskHealthStatus.Healthy => L("Label_HealthHealthy"),
        DiskHealthStatus.Warning => L("Label_HealthWarning"),
        DiskHealthStatus.Failed => L("Label_HealthFailed"),
        _ => L("Label_HealthUnknown")
    };
}
