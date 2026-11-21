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
    private SystemOverview? _overview;
    private string _realtimeStatus = string.Empty;

    public HardwareViewModel()
    {
        RefreshCommand = CreateCommand(LoadAsync);
        ToggleSensorCommand = CreateSyncCommand(ToggleSensor);
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

    /// <summary>数据新鲜度状态：实时 / 采集延迟 / 已过期，实时告知用户当前读数是否可信。</summary>
    public string RealtimeStatus { get => _realtimeStatus; private set => Set(ref _realtimeStatus, value); }

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
        _overview = o;
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

        // 显存优先取驱动注册表的 64 位精确值；只有回落到 WMI 的 uint32 时才标注可能截断，
        // 两者都不可用时显示 N/A，避免把「0 B」或回绕后的 2 GB 当成真实容量。
        GpuList = o.Gpus.Count == 0 ? L("Common_None") : string.Join(Environment.NewLine + Environment.NewLine,
            o.Gpus.Select(g => $"{g.Name}{Environment.NewLine}" +
                $"  {L("Hardware_GpuVram")}: {VramText(g)} {VramNote(g)}"));

        DiskList = o.Disks.Count == 0 ? L("Common_None") : string.Join(Environment.NewLine + Environment.NewLine,
            o.Disks.Select(d => $"[{d.MediaTypeText}] {L("Label_Disk")} {d.DiskIndex} {d.Model}  " +
                $"{FormatHelper.FormatBytes(d.TotalBytes)}  {d.InterfaceType}{Environment.NewLine}" +
                $"  {L("Hardware_DiskHealth")}: {HealthText(d.Health)} · {d.Smart.Summary}{Environment.NewLine}" +
                $"  SMART: {(d.Smart.IsAvailable ? $"{L("Label_PowerOn")} {d.Smart.PowerOnHours?.ToString() ?? "N/A"} h, {L("Label_Cycles")} {d.Smart.PowerCycleCount?.ToString() ?? "N/A"}" : L("Common_NA"))}{Environment.NewLine}" +
                string.Join(Environment.NewLine, d.Partitions.Select(p =>
                    $"  {p.DriveLetter} {p.FileSystem}  {FormatHelper.FormatBytes(p.UsedBytes)}/{FormatHelper.FormatBytes(p.TotalBytes)} ({p.UsedPercent:F1} %)"))));

        BatteryPresent = o.Battery.IsPresent;
        BatteryDesign = $"{o.Battery.DesignCapacityMwh} mWh";
        // 读不到满充容量时不能拿设计容量冒充满充容量（否则损耗率恒为 0%），显示未知。
        BatteryFull = o.Battery.IsFullChargeCapacityKnown
            ? $"{o.Battery.FullChargeCapacityMwh} mWh" : L("Common_NA");
        BatteryWear = o.Battery.IsFullChargeCapacityKnown
            ? $"{o.Battery.WearPercent:F1} %" : L("Common_NA");
        BatteryCycles = o.Battery.CycleCount >= 0 ? o.Battery.CycleCount.ToString() : L("Common_NA");
        // 与概览页/导出报告口径一致：满充容量未知时 ChargePercent 恒为 0，
        // 不能把"读不到"显示成"0% 电量耗尽"误导用户。
        BatteryCharge = o.Battery.IsFullChargeCapacityKnown
            ? $"{o.Battery.ChargePercent:F1} % ({(o.Battery.IsCharging ? L("Label_Charging") : L("Label_NotCharging"))})"
            : L("Common_NA");

        DisplayList = o.Displays.Count == 0 ? L("Common_None") : string.Join(Environment.NewLine,
            o.Displays.Select(d => $"{(d.IsPrimary ? "● " : "○ ")}{d.DeviceName}  {d.ResolutionText}  " +
                $"{d.BitsPerPixel} bit  {L("Hardware_DisplayHdr")}: {(d.HdrEnabled switch { true => L("Common_Yes"), false => L("Common_No"), _ => L("Common_NA") })}"));

        RaiseAll();
    }

    private void OnSnapshot(SensorSnapshot snapshot)
    {
        var count = snapshot.Readings.Count;
        PostUI(() =>
        {
            SensorCount = count.ToString();

            // 数据新鲜度：过期（采集停止 / 卡顿）优先提示；否则显示采集耗时与周期
            RealtimeStatus = !Services.Sensors.IsRunning || snapshot.IsStale
                ? L("Hardware_RealtimeStale")
                : LF("Hardware_RealtimeLag", snapshot.CollectionCostMs.ToString("F0"),
                    Services.Sensors.RefreshIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // 动态指标用同一采集窗口的快照实时刷新，不再依赖打开页面时的一次性 WMI 查询。
            // 这样 CPU / 内存读数与实时曲线严格同源、同一时刻，避免数据自相矛盾。
            CpuUsage = $"{snapshot.CpuUsagePercent:F1} %";
            MemUsed = snapshot.UsedMemoryBytes is { } used && snapshot.TotalMemoryBytes is { } total
                ? $"{FormatHelper.FormatBytes(used)} ({snapshot.MemoryUsagePercent:F1} %)"
                : $"{snapshot.MemoryUsagePercent:F1} %";
            if (snapshot.TotalMemoryBytes is { } totalMem) MemTotal = FormatHelper.FormatBytes(totalMem);
            if (snapshot.CpuTemperature is { } temp) CpuTemp = $"{temp:F1} °C";
            if (snapshot.CpuPower is { } power) CpuPower = $"{power:F1} W";
            if (snapshot.CpuClockMhz is { } clock) CpuClock = $"{clock:F0} MHz";

            var history = Services.SensorMonitor.History;
            // 优先展示温度、风扇、电压三类曲线，最多 6 条，避免图表过多影响流畅度
            var preferred = snapshot.Readings
                .Where(r => r.Kind is SensorKind.Temperature or SensorKind.Fan or SensorKind.Voltage)
                .GroupBy(r => r.Identifier)
                .Select(g => g.First())
                .Take(6)
                .ToList();

            // 驱动完全不可用、没有温度/风扇/电压读数时，用 Win32 的 CPU / 内存负载曲线兜底，
            // 保证实时面板在任何权限下都有可见数据，而不是一片空白。
            if (preferred.Count == 0)
            {
                preferred = snapshot.Readings
                    .Where(r => r.Kind == SensorKind.Load)
                    .GroupBy(r => r.Identifier)
                    .Select(g => g.First())
                    .Take(2)
                    .ToList();
            }

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

    protected override void OnLanguageChanged()
    {
        if (_overview is not null) Apply(_overview);
    }

    private static string VramText(GpuInfo gpu)
        => gpu.VramTotalBytes > 0 ? FormatHelper.FormatBytes(gpu.VramTotalBytes) : L("Common_NA");

    private static string VramNote(GpuInfo gpu) => gpu.VramSource switch
    {
        "Registry" => L("Hardware_VramSourceRegistry"),
        "WMI" => L("Hardware_GpuVramNote"),
        _ => L("Hardware_VramUnknown")
    };

    private static string HealthText(DiskHealthStatus status) => status switch
    {
        DiskHealthStatus.Healthy => L("Label_HealthHealthy"),
        DiskHealthStatus.Warning => L("Label_HealthWarning"),
        DiskHealthStatus.Failed => L("Label_HealthFailed"),
        _ => L("Label_HealthUnknown")
    };

    public override void Dispose()
    {
        Services.Sensors.SnapshotUpdated -= OnSnapshot;
        base.Dispose();
    }
}
