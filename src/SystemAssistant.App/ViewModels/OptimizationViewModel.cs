using System.Windows.Input;
using Avalonia.Threading;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.App.ViewModels;

/// <summary>
/// 系统优化页 ViewModel。所有变更命令都会先走二次确认；权限不足时对应按钮被禁用并提示。
/// </summary>
public sealed class OptimizationViewModel : ViewModelBase
{
    private IReadOnlyList<StartupItem> _startupItems = Array.Empty<StartupItem>();
    private IReadOnlyList<WindowsServiceInfo> _services = Array.Empty<WindowsServiceInfo>();
    private IReadOnlyList<TuningItem> _tuningItems = Array.Empty<TuningItem>();
    private IReadOnlyList<DiskInfo> _disks = Array.Empty<DiskInfo>();
    private StartupItem? _selectedStartup;
    private WindowsServiceInfo? _selectedService;
    private DiskInfo? _selectedDisk;
    private string _consoleText = string.Empty;
    private string _pingHost = "www.baidu.com";
    private string _pingResult = string.Empty;
    private string _virtualMemory = string.Empty;
    private string _serviceRiskHint = string.Empty;
    // 缓存组合文本的原始参数，供语言切换后按当前语言重算
    private (bool IsAutomatic, long InitialMb, long MaximumMb, string Description)? _virtualMemoryInfo;
    private (string Host, double Latency, double PacketLoss)? _pingInfo;

    public OptimizationViewModel()
    {
        RefreshStartupCommand = CreateCommand(LoadStartupAsync);
        RefreshServicesCommand = CreateCommand(LoadServicesAsync);
        EnableStartupCommand = CreateCommand(async p => await SetStartupEnabledAsync(p, true));
        DisableStartupCommand = CreateCommand(async p => await SetStartupEnabledAsync(p, false));
        DeleteStartupCommand = CreateCommand(async p => await DeleteStartupAsync(p));
        StartServiceCommand = CreateCommand(async p => await ChangeServiceAsync(p, true));
        StopServiceCommand = CreateCommand(async p => await ChangeServiceAsync(p, false));
        SetStartModeCommand = CreateCommand(async p => await SetStartModeAsync(p));
        SfcCommand = CreateCommand(async () => await RunRepairAsync(true));
        DismCommand = CreateCommand(async () => await RunRepairAsync(false));
        DiskMaintenanceCommand = CreateCommand(async p => await MaintainDiskAsync(p));
        FlushDnsCommand = CreateCommand(FlushDnsAsync);
        ResetWinsockCommand = CreateCommand(ResetWinsockAsync);
        ResetTcpIpCommand = CreateCommand(ResetTcpIpAsync);
        PingCommand = CreateCommand(PingAsync);
        ToggleTuningCommand = CreateCommand(async p => await ToggleTuningAsync(p));
        OpenVirtualMemoryCommand = new RelayCommand(() =>
        {
            if (!ShellHelper.OpenSystemTool("virtualmemory"))
                StatusMessage = L("Status_OpenToolFailed");
        });
        RefreshTuningCommand = new RelayCommand(LoadTuning);

        LoadTuning();
        Services.Repair.OutputReceived += OnCommandOutput;
        Services.DiskMaintenance.OutputReceived += OnCommandOutput;
        _ = LoadStartupAsync();
        _ = LoadServicesAsync();
        _ = LoadDisksAsync();
    }

    public event Action<string, bool>? OutputLine;

    public ICommand RefreshStartupCommand { get; }
    public ICommand RefreshServicesCommand { get; }
    public ICommand EnableStartupCommand { get; }
    public ICommand DisableStartupCommand { get; }
    public ICommand DeleteStartupCommand { get; }
    public ICommand StartServiceCommand { get; }
    public ICommand StopServiceCommand { get; }
    public ICommand SetStartModeCommand { get; }
    public ICommand SfcCommand { get; }
    public ICommand DismCommand { get; }
    public ICommand DiskMaintenanceCommand { get; }
    public ICommand FlushDnsCommand { get; }
    public ICommand ResetWinsockCommand { get; }
    public ICommand ResetTcpIpCommand { get; }
    public ICommand PingCommand { get; }
    public ICommand ToggleTuningCommand { get; }
    public ICommand OpenVirtualMemoryCommand { get; }
    public ICommand RefreshTuningCommand { get; }

    public bool IsAdministrator => PrivilegeHelper.IsAdministrator;
    public string AdminHint => IsAdministrator ? string.Empty : L("Common_AdminRequired");

    public IReadOnlyList<StartupItem> StartupItems { get => _startupItems; private set => Set(ref _startupItems, value); }
    public IReadOnlyList<WindowsServiceInfo> ServicesList { get => _services; private set => Set(ref _services, value); }
    public IReadOnlyList<TuningItem> TuningItems { get => _tuningItems; private set => Set(ref _tuningItems, value); }
    public IReadOnlyList<DiskInfo> Disks { get => _disks; private set => Set(ref _disks, value); }

    public StartupItem? SelectedStartup { get => _selectedStartup; set => Set(ref _selectedStartup, value); }
    public WindowsServiceInfo? SelectedService
    {
        get => _selectedService;
        set
        {
            if (!Set(ref _selectedService, value)) return;
            ServiceRiskHint = value?.IsHighRisk == true ? LF("Optimize_RiskWarning", value.RiskHint) : string.Empty;
        }
    }
    public DiskInfo? SelectedDisk { get => _selectedDisk; set => Set(ref _selectedDisk, value); }
    public string ConsoleText { get => _consoleText; private set => Set(ref _consoleText, value); }
    public string PingHost { get => _pingHost; set => Set(ref _pingHost, value); }
    public string PingResult { get => _pingResult; private set => Set(ref _pingResult, value); }
    public string VirtualMemory { get => _virtualMemory; private set => Set(ref _virtualMemory, value); }
    public string ServiceRiskHint { get => _serviceRiskHint; private set => Set(ref _serviceRiskHint, value); }

    public IReadOnlyList<string> StartModes { get; } = new[]
    {
        "Automatic", "AutomaticDelayedStart", "Manual", "Disabled"
    };

    private async Task LoadStartupAsync()
        => await RunBusyAsync(async () => StartupItems = await Services.Startup.GetStartupItemsAsync());

    private async Task LoadServicesAsync()
        => await RunBusyAsync(async () => ServicesList = await Services.Services.GetServicesAsync());

    private async Task LoadDisksAsync()
    {
        await RunBusyAsync(async () =>
        {
            var disks = await Services.Hardware.GetDisksAsync();
            Disks = disks.Where(d => d.Partitions.Count > 0).ToArray();
            SelectedDisk = Disks.FirstOrDefault();
        });
    }

    private void LoadTuning()
    {
        TuningItems = Services.Tuning.GetTuningItems();
        var vm = Services.Tuning.GetVirtualMemoryInfo();
        _virtualMemoryInfo = (vm.IsAutomatic, vm.InitialSizeMb, vm.MaximumSizeMb, vm.Description);
        VirtualMemory = BuildVirtualMemoryText();
    }

    private async Task SetStartupEnabledAsync(object? parameter, bool enabled)
    {
        var item = parameter as StartupItem ?? SelectedStartup;
        if (item is null) return;
        var action = $"{(enabled ? L("Common_Enable") : L("Common_Disable"))} {item.Name}";
        if (!await ConfirmAsync(action, item.Command)) return;
        var result = await Services.Startup.SetEnabledAsync(item, enabled);
        StatusMessage = result.Message;
        await LoadStartupAsync();
    }

    private async Task DeleteStartupAsync(object? parameter)
    {
        var item = parameter as StartupItem ?? SelectedStartup;
        if (item is null) return;
        var action = $"{L("Common_Delete")} {item.Name}";
        if (!await ConfirmAsync(action, item.Command)) return;
        var result = await Services.Startup.DeleteAsync(item);
        StatusMessage = result.Message;
        await LoadStartupAsync();
    }

    private async Task ChangeServiceAsync(object? parameter, bool start)
    {
        var service = parameter as WindowsServiceInfo ?? SelectedService;
        if (service is null) return;
        var action = $"{(start ? L("Optimize_Start") : L("Optimize_Stop"))} {service.DisplayName}";
        var detail = $"{service.ServiceName}{Environment.NewLine}{service.Description}";
        if (!await ConfirmAsync(action, detail + (service.IsHighRisk ? Environment.NewLine + service.RiskHint : ""))) return;
        var result = start
            ? await Services.Services.StartAsync(service.ServiceName)
            : await Services.Services.StopAsync(service.ServiceName);
        StatusMessage = result.Message;
        await LoadServicesAsync();
    }

    private async Task SetStartModeAsync(object? parameter)
    {
        var modeText = parameter as string ?? "Manual";
        if (!Enum.TryParse<ServiceStartMode>(modeText, out var mode)) return;
        if (SelectedService is null) return;
        var service = SelectedService;
        var action = $"{L("Optimize_StartMode")} {service.DisplayName} → {modeText}";
        if (!await ConfirmAsync(action, service.ServiceName + Environment.NewLine + service.RiskHint)) return;
        var result = await Services.Services.SetStartModeAsync(service.ServiceName, mode);
        StatusMessage = result.Message;
        await LoadServicesAsync();
    }

    private async Task RunRepairAsync(bool sfc)
    {
        if (!IsAdministrator) { StatusMessage = L("Common_AdminRequired"); return; }
        var action = sfc ? "sfc /scannow" : "DISM /Online /Cleanup-Image /RestoreHealth";
        if (!await ConfirmAsync(action, L("Optimize_Console"))) return;
        await RunBusyAsync(async () =>
        {
            ConsoleText = string.Empty;
            var result = sfc
                ? await Services.Repair.RunSfcScanNowAsync()
                : await Services.Repair.RunDismRestoreHealthAsync();
            StatusMessage = $"{action} → {result.ExitCode}";
            if (!result.Succeeded) await DialogService.ErrorAsync(result.StandardError);
        }, action);
    }

    private async Task MaintainDiskAsync(object? parameter)
    {
        if (!IsAdministrator) { StatusMessage = L("Common_AdminRequired"); return; }
        if (SelectedDisk is null) return;
        var disk = SelectedDisk;
        // 首个分区存在但 DriveLetter 为空串时，FirstOrDefault() 会得到 '\0'（非法盘符）。
        // 只有确实读到 A-Z 字母才采用，否则回退 C:。
        var firstPartition = disk.Partitions.FirstOrDefault();
        var letter = firstPartition?.DriveLetter.FirstOrDefault() ?? '\0';
        var drive = letter is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? letter : 'C';
        var mode = parameter as string ?? "analyze";

        // Core 的 MaintainAsync 只按介质类型决定动作，会丢弃按钮传入的 trim/defrag 语义，
        // 导致 HDD 点 TRIM 也跑碎片整理。调用前校验用户选择与介质是否匹配，不匹配直接提示并返回。
        if (mode == "trim" && disk.MediaType != DiskMediaType.SolidStateDrive)
        {
            StatusMessage = LF("Optimize_DiskActionMismatch", L("Optimize_Trim"), disk.MediaTypeText);
            return;
        }
        if (mode == "defrag" && disk.MediaType != DiskMediaType.HardDiskDrive)
        {
            StatusMessage = LF("Optimize_DiskActionMismatch", L("Optimize_Defrag"), disk.MediaTypeText);
            return;
        }

        var isTrim = disk.MediaType == DiskMediaType.SolidStateDrive;
        var action = mode == "analyze" ? L("Optimize_Analyze")
            : isTrim ? L("Optimize_Trim") : L("Optimize_Defrag");
        if (!await ConfirmAsync($"{action} ({drive}:)", disk.Model)) return;
        await RunBusyAsync(async () =>
        {
            ConsoleText = string.Empty;
            var result = await Services.DiskMaintenance.MaintainAsync(
                disk.DiskIndex, drive, disk.MediaType, mode == "analyze");
            StatusMessage = $"{action} → {result.ExitCode}";
            if (!result.Succeeded) await DialogService.ErrorAsync(result.StandardError);
        }, action);
    }

    private async Task FlushDnsAsync()
        => await RunSimpleNetworkAsync(L("Optimize_FlushDns"),
            () => Services.Network.FlushDnsAsync(), requiresAdmin: false);

    private async Task ResetWinsockAsync()
        => await RunSimpleNetworkAsync(L("Optimize_ResetWinsock"),
            () => Services.Network.ResetWinsockAsync());

    private async Task ResetTcpIpAsync()
        => await RunSimpleNetworkAsync(L("Optimize_ResetTcpIp"),
            () => Services.Network.ResetTcpIpAsync());

    private async Task RunSimpleNetworkAsync(string action, Func<Task<CommandExecutionResult>> work,
        bool requiresAdmin = true)
    {
        // 刷新 DNS 不需要管理员；其余网络重置命令仍需提权，命令内部也要拦截，不能只依赖界面禁用。
        if (requiresAdmin && !IsAdministrator) { StatusMessage = L("Common_AdminRequired"); return; }
        if (!await ConfirmAsync(action, L("Common_Risk") + ": " + L("Optimize_Subtitle"))) return;
        var result = await work();
        StatusMessage = $"{action} → {result.ExitCode}";
        if (!result.Succeeded) await DialogService.ErrorAsync(result.StandardError);
    }

    private async Task PingAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await Services.Network.TestLatencyAsync(PingHost);
            _pingInfo = (result.Host, result.AverageLatencyMs, result.PacketLossPercent);
            PingResult = BuildPingResultText();
            ConsoleText = result.RawOutput;
        }, L("Optimize_Ping"));
    }

    private async Task ToggleTuningAsync(object? parameter)
    {
        if (parameter is not TuningItem item) return;
        var target = !item.CurrentValue;
        var action = $"{item.Name} → {(target ? L("Common_Enable") : L("Common_Disable"))}";
        if (!await ConfirmAsync(action, item.Description + Environment.NewLine + item.RiskText)) return;
        var result = Services.Tuning.Apply(item, target);
        StatusMessage = result.Success ? L("Status_Done") : result.Message;
        if (!result.Success) await DialogService.ErrorAsync(result.Message);
        LoadTuning();
    }

    private async Task<bool> ConfirmAsync(string action, string detail)
        => await DialogService.ConfirmHighRiskAsync(action, detail);

    private string BuildVirtualMemoryText() => _virtualMemoryInfo is { } v
        ? $"{(v.IsAutomatic ? L("Label_AutoManaged") : $"{v.InitialMb} ~ {v.MaximumMb} MB")} · {v.Description}"
        : string.Empty;

    private string BuildPingResultText() => _pingInfo is { } p
        ? $"{p.Host} · {p.Latency} ms · {L("Label_PacketLoss")} {p.PacketLoss} %"
        : string.Empty;

    protected override void OnLanguageChanged()
    {
        VirtualMemory = BuildVirtualMemoryText();
        PingResult = BuildPingResultText();
        ServiceRiskHint = SelectedService?.IsHighRisk == true
            ? LF("Optimize_RiskWarning", SelectedService.RiskHint)
            : string.Empty;
    }

    private void OnCommandOutput(CommandOutputLine line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var text = $"{line.Timestamp:HH:mm:ss} {(line.IsError ? "[!] " : "    ")}{line.Text}";
            ConsoleText = (ConsoleText + Environment.NewLine + text).TrimStart();
            if (ConsoleText.Length > 60_000) ConsoleText = ConsoleText[^60_000..];
            OutputLine?.Invoke(line.Text, line.IsError);
        });
    }

    public override void Dispose()
    {
        Services.Repair.OutputReceived -= OnCommandOutput;
        Services.DiskMaintenance.OutputReceived -= OnCommandOutput;
        base.Dispose();
    }
}
