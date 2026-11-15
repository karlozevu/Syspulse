using System.Windows.Input;
using Avalonia.Threading;
using SystemAssistant.App.Mvvm;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.App.ViewModels;

/// <summary>一条曲线的数据。</summary>
public sealed class SensorChartVm
{
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public required string Unit { get; init; }
    public required IReadOnlyList<double> Values { get; init; }
    public int ColorIndex { get; init; }
}

/// <summary>硬件详情与传感器实时监控页 ViewModel。</summary>
public sealed class HardwareViewModel : ViewModelBase
{
    private string _cpuName = string.Empty;
    private string _cpuCores = string.Empty;
    private string _cpuBase = string.Empty;
    private string _cpuTurbo = string.Empty;
    private string _cpuCache = string.Empty;
    private string _cpuUsage = string.Empty;
    private string _cpuClock = string.Empty;
    private string _cpuTemp = string.Empty;
    private string _cpuPower = string.Empty;
    private string _memTotal = string.Empty;
    private string _memUsed = string.Empty;
    private string _memFree = string.Empty;
    private string _memPage = string.Empty;
    private string _memChannel = string.Empty;
    private string _memModules = string.Empty;
    private string _gpuList = string.Empty;
    private string _diskList = string.Empty;
    private string _batteryDesign = string.Empty;
    private string _batteryFull = string.Empty;
    private string _batteryWear = string.Empty;
    private string _batteryCycles = string.Empty;
    private string _batteryCharge = string.Empty;
    private bool _batteryPresent;
    private string _displayList = string.Empty;
    private string _sensorCount = "0";
    private bool _isMonitoring;

    public HardwareViewModel()
    {
        RefreshCommand = CreateCommand(LoadAsync);
        ToggleSensorCommand = new RelayCommand(ToggleSensor);
        Services.Sensors.SnapshotUpdated += OnSnapshot;
        _isMonitoring = Services.Sensors.IsRunning;
        _ = LoadAsync();
    }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleSensorCommand { get; }

    /// <summary>曲线数据更新事件（UI 线程触发），页面据此刷新自绘折线图。</summary>
    public event Action<IReadOnlyList<SensorChartVm>>? ChartsUpdated;

    public IReadOnlyList<SensorChartVm> CurrentCharts { get; private set; } = Array.Empty<SensorChartVm>();

    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }
    public string CpuCores { get => _cpuCores; private set => Set(ref _cpuCores, value); }
    public string CpuBase { get => _cpuBase; private set => Set(ref _cpuBase, value); }
    public string CpuTurbo { get => _cpuTurbo; private set => Set(ref _cpuTurbo, value); }
    public string CpuCache { get => _cpuCache; private set => Set(ref _cpuCache, value); }
    public string CpuUsage { get => _cpuUsage; private set => Set(ref _cpuUsage, value); }
    public string CpuClock { get => _cpuClock; private set => Set(ref _cpuClock, value); }
    public string CpuTemp { get => _cpuTemp; private set => Set(ref _cpuTemp, value); }
    public string CpuPower { get => _cpuPower; private set => Set(ref _cpuPower, value); }
    public string MemTotal { get => _memTotal; private set => Set(ref _memTotal, value); }
    public string MemUsed { get => _memUsed; private set => Set(ref _memUsed, value); }
    public string MemFree { get => _memFree; private set => Set(ref _memFree, value); }
    public string MemPage { get => _memPage; private set => Set(ref _memPage, value); }
    public string MemChannel { get => _memChannel; private set => Set(ref _memChannel, value); }
    public string MemModules { get => _memModules; private set => Set(ref _memModules, value); }
    public string GpuList { get => _gpuList; private set => Set(ref _gpuList, value); }
    public string DiskList { get => _diskList; private set => Set(ref _diskList, value); }
    public string BatteryDesign { get => _batteryDesign; private set => Set(ref _batteryDesign, value); }
    public string BatteryFull { get => _batteryFull; private set => Set(ref _batteryFull, value); }
    public string BatteryWear { get => _batteryWear; private set => Set(ref _batteryWear, value); }
    public string BatteryCycles { get => _batteryCycles; private set => Set(ref _batteryCycles, value); }
    public string BatteryCharge { get => _batteryCharge; private set => Set(ref _batteryCharge, value); }
    public bool BatteryPresent { get => _batteryPresent; private set => Set(ref _batteryPresent, value); }
    public string DisplayList { get => _displayList; private set => Set(ref _displayList, value); }
    public string SensorCount { get => _sensorCount; private set => Set(ref _sensorCount, value); }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (Set(ref _isMonitoring, value)) Raise(nameof(SensorButtonKey));
        }
    }

    public string SensorButtonKey => IsMonitoring ? "Hardware_SensorsStop" : "Hardware_SensorsStart";

    private void ToggleSensor()
    {
        if (IsMonitoring) Services.Sensors.Stop();
        else Services.Sensors.Start();
        IsMonitoring = Services.Sensors.IsRunning;
        StatusMessage = IsMonitoring ? L("Hardware_SensorsStart") : L("Hardware_SensorsStop");
    }

    private async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            var overview = await Services.Hardware.GetOverviewAsync();
            Apply(overview);
            StatusMessage = L("Status_Done");
        });
    }

    private void Apply(SystemOverview o)
    {
        CpuName = o.Cpu.Name;
        CpuCores = $"{o.Cpu.PhysicalCores} / {o.Cpu.LogicalProcessors}";
        CpuBase = $"{o.Cpu.BaseClockMHz:F0} MHz";
        CpuTurbo = o.Cpu.MaxTurboMHz > 0 ? $"{o.Cpu.MaxTurboMHz:F0} MHz" : L("Common_NA");
        CpuCache = $"L1 {o.Cpu.L1CacheKB} KB / L2 {o.Cpu.L2CacheKB} KB / L3 {o.Cpu.L3CacheKB} KB";
        CpuUsage = $"{o.Cpu.UsagePercent:F1} %";
        CpuClock = $"{o.Cpu.CurrentClockMHz:F0} MHz";
        CpuTemp = o.Cpu.TemperatureCelsius.HasValue ? $"{o.Cpu.TemperatureCelsius:F1} °C" : L("Common_NA");
        CpuPower = o.Cpu.PackagePowerWatt.HasValue ? $"{o.Cpu.PackagePowerWatt:F1} W" : L("Common_NA");

        MemTotal = FormatHelper.FormatBytes(o.Memory.TotalBytes);
        MemUsed = $"{FormatHelper.FormatBytes(o.Memory.UsedBytes)} ({o.Memory.UsagePercent:F1} %)";
        MemFree = FormatHelper.FormatBytes(o.Memory.AvailableBytes);
        MemPage = $"{FormatHelper.FormatBytes(o.Memory.PageFileUsedBytes)} / {FormatHelper.FormatBytes(o.Memory.PageFileTotalBytes)}";
        MemChannel = o.Memory.ChannelDescription;
        MemModules = o.Memory.Modules.Count == 0
            ? L("Common_None")
            : string.Join("；", o.Memory.Modules.Select(m =>
                $"{m.Slot}: {m.Manufacturer} {FormatHelper.FormatBytes(m.CapacityBytes)} @ {m.SpeedMHz} MHz"));

        GpuList = o.Gpus.Count == 0 ? L("Common_None") : string.Join(Environment.NewLine + Environment.NewLine,
            o.Gpus.Select(g => $"{g.Name}{Environment.NewLine}" +
                $"  {L("Hardware_GpuVram")}: {FormatHelper.FormatBytes(g.VramTotalBytes)} / " +
                $"{L("Hardware_GpuVramUsed")}: {(g.VramUsedBytes.HasValue ? FormatHelper.FormatBytes(g.VramUsedBytes.Value) : L("Common_NA"))} / " +
                $"{L("Hardware_GpuLoad")}: {(g.LoadPercent.HasValue ? $"{g.LoadPercent:F1} %" : L("Common_NA"))} / " +
                $"{L("Hardware_GpuTemp")}: {(g.TemperatureCelsius.HasValue ? $"{g.TemperatureCelsius:F1} °C" : L("Common_NA"))}"));

        DiskList = o.Disks.Count == 0 ? L("Common_None") : string.Join(Environment.NewLine + Environment.NewLine,
            o.Disks.Select(d => $"[{d.MediaTypeText}] 磁盘 {d.DiskIndex} {d.Model}  " +
                $"{FormatHelper.FormatBytes(d.TotalBytes)}  {d.InterfaceType}{Environment.NewLine}" +
                $"  {L("Hardware_DiskHealth")}: {HealthText(d.Health)} · {d.Smart.Summary}{Environment.NewLine}" +
                $"  SMART: {(d.Smart.IsAvailable ? $"通电 {d.Smart.PowerOnHours?.ToString() ?? "N/A"} h，循环 {d.Smart.PowerCycleCount?.ToString() ?? "N/A"}" : L("Common_NA"))}{Environment.NewLine}" +
                string.Join(Environment.NewLine, d.Partitions.Select(p =>
                    $"  {p.DriveLetter} {p.FileSystem}  {FormatHelper.FormatBytes(p.UsedBytes)}/{FormatHelper.FormatBytes(p.TotalBytes)} ({p.UsedPercent:F1} %)"))));

        BatteryPresent = o.Battery.IsPresent;
        BatteryDesign = $"{o.Battery.DesignCapacityMwh} mWh";
        BatteryFull = $"{o.Battery.FullChargeCapacityMwh} mWh";
        BatteryWear = $"{o.Battery.WearPercent:F1} %";
        BatteryCycles = o.Battery.CycleCount >= 0 ? o.Battery.CycleCount.ToString() : L("Common_NA");
        BatteryCharge = $"{o.Battery.ChargePercent:F1} % ({(o.Battery.IsCharging ? "充电中" : "未充电")})";

        DisplayList = o.Displays.Count == 0 ? L("Common_None") : string.Join(Environment.NewLine,
            o.Displays.Select(d => $"{(d.IsPrimary ? "● " : "○ ")}{d.DeviceName}  {d.ResolutionText}  " +
                $"{d.BitsPerPixel} bit  {L("Hardware_DisplayHdr")}: {(d.HdrEnabled switch { true => L("Common_Yes"), false => L("Common_No"), _ => L("Common_NA") })}"));

        RaiseAll();
    }

    private void OnSnapshot(SensorSnapshot snapshot)
    {
        var count = snapshot.Readings.Count;
        Dispatcher.UIThread.Post(() =>
        {
            SensorCount = count.ToString();
            var history = Services.SensorMonitor.History;
            // 优先展示温度、风扇、电压三类曲线，最多 6 条，避免图表过多影响流畅度
            var preferred = snapshot.Readings
                .Where(r => r.Kind is SensorKind.Temperature or SensorKind.Fan or SensorKind.Voltage)
                .GroupBy(r => r.Identifier)
                .Select(g => g.First())
                .Take(6)
                .ToList();

            var charts = new List<SensorChartVm>();
            for (var i = 0; i < preferred.Count; i++)
            {
                var reading = preferred[i];
                charts.Add(new SensorChartVm
                {
                    Identifier = reading.Identifier,
                    Title = reading.DisplayName,
                    Unit = reading.Unit,
                    Values = history.GetSeries(reading.Identifier),
                    ColorIndex = i
                });
            }
            CurrentCharts = charts;
            ChartsUpdated?.Invoke(charts);
        });
    }

    private static string HealthText(DiskHealthStatus status) => status switch
    {
        DiskHealthStatus.Healthy => "健康",
        DiskHealthStatus.Warning => "⚠ 警告",
        DiskHealthStatus.Failed => "故障",
        _ => "未知"
    };

    public override void Dispose()
    {
        Services.Sensors.SnapshotUpdated -= OnSnapshot;
        base.Dispose();
    }
}
