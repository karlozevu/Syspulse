using System.Collections.Concurrent;
using LibreHardwareMonitor.Hardware;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.Core.Hardware;

/// <summary>
/// 传感器持续采集器。基于 LibreHardwareMonitorLib，在独立后台任务里按刷新间隔采样，
/// 通过事件把快照推送给 UI；UI 线程永远不会被硬件读取阻塞。
/// 采集失败（无管理员权限、驱动缺失等）时只记录 LastError，不会抛出异常。
/// </summary>
public sealed class SensorMonitor : ISensorMonitor, IDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAlert = new();
    private Computer? _computer;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private IReadOnlyList<SensorThreshold> _thresholds = Array.Empty<SensorThreshold>();
    private IReadOnlyList<SensorReading> _latest = Array.Empty<SensorReading>();
    private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(2);

    // LHM 不可用时的降级读数使用固定标识，保证曲线连续、LatestReadings 恒含 CPU / 内存两项
    private const string NativeCpuIdentifier = "native/cpu/load";
    private const string NativeMemoryIdentifier = "native/memory/load";

    // GetSystemTimes 首次 / 偶发失败时沿用上一次有效值，避免快照出现空白（仅后台工作线程读写）
    private double _lastCpuUsage;
    private bool _hasCpuSample;

    public event Action<SensorSnapshot>? SnapshotUpdated;
    public event Action<ThresholdAlert>? ThresholdExceeded;

    public bool IsRunning => _worker is { IsCompleted: false };

    public int RefreshIntervalMs { get; set; } = 2000;

    /// <summary>最近一次采集到的全部传感器读数（只读快照，线程安全）。</summary>
    public IReadOnlyList<SensorReading> LatestReadings => Volatile.Read(ref _latest);

    /// <summary>最近一次采集错误，供界面显示降级提示。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>传感器历史序列，供曲线控件读取。</summary>
    public SensorHistory History { get; } = new(120);

    public void SetThresholds(IReadOnlyList<SensorThreshold> thresholds)
    {
        lock (_sync) _thresholds = thresholds.ToArray();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => RunLoopAsync(token), token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_sync)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }
        cts?.Cancel();
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        cts?.Dispose();
        CloseComputer();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            OpenComputer();
            // 预热 GetSystemTimes 基准：该方法首次调用只记录采样、返回 null，
            // 之后每个周期的差值才是真实 CPU 占用；预热后首个周期就能给出有效数值。
            var warmup = WindowsNativeInfo.GetCpuUsagePercent("sensor-monitor");
            if (warmup is not null) { _lastCpuUsage = warmup.Value; _hasCpuSample = true; }
            // 立即推送首个快照，不等待第一个计时周期，确保启动后很快就有数据
            CollectOnce();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(RefreshIntervalMs, 500, 60_000)));
            while (await timer.WaitForNextTickAsync(token))
            {
                // 用户可在设置页即时修改刷新间隔；定时器周期可以动态调整
                CollectOnce();
                if (Math.Abs(timer.Period.TotalMilliseconds - RefreshIntervalMs) > 1)
                {
                    timer.Period = TimeSpan.FromMilliseconds(Math.Clamp(RefreshIntervalMs, 500, 60_000));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            // 采集循环内的任何异常都必须被观察，避免变成进程级未观察任务异常
            LastError = "传感器采集已停止：" + ex.Message;
        }
        finally
        {
            CloseComputer();
        }
    }

    private void OpenComputer()
    {
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = true,
                IsBatteryEnabled = true
            };
            computer.Open();
            lock (_sync)
            {
                // 若上一次实例尚未释放，先释放再替换，避免驱动句柄泄漏
                ReleaseComputer(_computer);
                _computer = computer;
            }
            computer = null; // 所有权已移交，交给 CloseComputer 统一释放
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            // Open() 失败时 computer 尚未登记到 _computer，必须在这里就释放掉，
            // 否则 Ring0.Open() 已创建的资源会随未引用的实例一起泄漏。
            ReleaseComputer(computer);
            // 常见于未以管理员权限运行：部分主板 / 风扇传感器不可用
            LastError = "传感器初始化受限：" + ex.Message;
        }
    }

    /// <summary>
    /// 释放 Computer 资源。LibreHardwareMonitorLib 0.9.4 的 Computer 未实现 IDisposable，
    /// 其公开的 Close() 才是释放入口（关闭 Ring0 驱动句柄、内核互斥体与全部硬件组），
    /// 因此这里用 Close() 作为 Dispose 的等价实现。
    /// </summary>
    private static void ReleaseComputer(Computer? computer)
    {
        if (computer is null) return;
        try { computer.Close(); }
        catch (Exception) { /* 释放失败不影响后续重建 */ }
    }

    private void CloseComputer()
    {
        lock (_sync)
        {
            ReleaseComputer(_computer);
            _computer = null;
        }
    }

    /// <summary>
    /// 执行一次采集。无论 LibreHardwareMonitor（Ring0 驱动）是否可用，都会构造并推送快照：
    /// CPU / 内存占用始终由 Win32 API 提供（普通权限可用），LHM 只负责温度 / 风扇 / 电压等附加读数。
    /// 这样实时曲线、传感器计数、阈值告警在任何权限下都能工作，不会因 LHM 初始化失败而全线空白。
    /// </summary>
    private void CollectOnce()
    {
        Computer? computer;
        lock (_sync) computer = _computer;

        var readings = new List<SensorReading>();
        string? degradedError = null;

        if (computer is not null)
        {
            try
            {
                foreach (var hardware in computer.Hardware)
                {
                    try { hardware.Update(); } catch { continue; }
                    AddSensors(readings, hardware.HardwareType.ToString(), hardware.Sensors);
                    foreach (var subHardware in hardware.SubHardware)
                    {
                        try { subHardware.Update(); } catch { continue; }
                        AddSensors(readings, hardware.HardwareType.ToString(), subHardware.Sensors);
                    }
                }
            }
            catch (Exception ex)
            {
                // 单次 LHM 读取失败只记录原因，不阻断本轮 Win32 基础快照的推送
                degradedError = "传感器读取中断：" + ex.Message;
            }
        }
        else
        {
            // LHM 不可用：沿用 OpenComputer 写入的初始化失败原因（若为空则给出降级说明）
            degradedError = string.IsNullOrWhiteSpace(LastError)
                ? "硬件传感器不可用（可能需要管理员权限），已降级为 Win32 基础监控（CPU / 内存）"
                : LastError;
        }

        // Win32 基础指标：普通权限即可读取，是快照的权威数据来源，绝不依赖 LHM
        var cpuSample = WindowsNativeInfo.GetCpuUsagePercent("sensor-monitor");
        if (cpuSample is not null) { _lastCpuUsage = cpuSample.Value; _hasCpuSample = true; }
        else if (!_hasCpuSample) { _lastCpuUsage = ReadCpuUsageFromWmi(); _hasCpuSample = true; }
        var cpuUsage = Math.Clamp(_lastCpuUsage, 0, 100);
        var memoryUsage = ReadMemoryUsagePercent();

        // 缺少 LHM 的 CPU / 内存负载读数时补上 Win32 原生读数，
        // 保证 LatestReadings 至少有 CPU 与内存两条，且曲线标识稳定连续。
        EnsureNativeLoadReadings(readings, cpuUsage, memoryUsage);

        var now = DateTime.Now;
        foreach (var reading in readings) reading.Timestamp = now;
        // LatestReadings 与事件载荷共用同一份数组快照：早先直接暴露可变 List，
        // 界面在 Stop/Dispose 之后可能读到空集合，导致 CPU 温度等派生字段恒为 N/A。
        var snapshotReadings = readings.ToArray();
        Volatile.Write(ref _latest, snapshotReadings);
        History.Add(snapshotReadings);

        // 失败原因只记录、不阻断快照推送
        LastError = degradedError ?? string.Empty;

        var snapshot = new SensorSnapshot
        {
            Timestamp = now,
            Readings = snapshotReadings,
            CpuUsagePercent = cpuUsage,
            MemoryUsagePercent = memoryUsage
        };
        RaiseSnapshotUpdated(snapshot);
        CheckThresholds(readings, now);
    }

    /// <summary>由 GlobalMemoryStatusEx 计算物理内存占用百分比（0~100）。</summary>
    private static double ReadMemoryUsagePercent()
    {
        var (totalPhys, availablePhys, _, _) = WindowsNativeInfo.GetMemoryStatus();
        if (totalPhys <= 0) return 0;
        return Math.Clamp((1 - availablePhys * 1.0 / totalPhys) * 100, 0, 100);
    }

    /// <summary>
    /// GetSystemTimes 尚未建立基准或不可用时，回落到 WMI 的 LoadPercentage（普通权限可读），
    /// 避免首个快照出现 CPU 恒 0。
    /// </summary>
    private static double ReadCpuUsageFromWmi()
        => Math.Clamp(WmiQuery.First(WmiQuery.CimV2, "Win32_Processor")?.Double("LoadPercentage") ?? 0, 0, 100);

    /// <summary>
    /// 若本轮没有 LHM 的 CPU Total / Memory 负载读数，就补上 Win32 原生读数。
    /// 固定 Identifier 保证 SensorHistory 曲线连续、LatestReadings 恒含 CPU / 内存两项。
    /// </summary>
    private static void EnsureNativeLoadReadings(List<SensorReading> readings, double cpuUsage, double memoryUsage)
    {
        if (!readings.Any(r => r.Kind == SensorKind.Load
            && r.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase)))
        {
            readings.Add(new SensorReading
            {
                Identifier = NativeCpuIdentifier,
                Name = "CPU Total",
                HardwareName = "CPU",
                Kind = SensorKind.Load,
                Unit = "%",
                Value = cpuUsage
            });
        }
        if (!readings.Any(r => r.Kind == SensorKind.Load
            && r.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)))
        {
            readings.Add(new SensorReading
            {
                Identifier = NativeMemoryIdentifier,
                Name = "Memory",
                HardwareName = "Memory",
                Kind = SensorKind.Load,
                Unit = "%",
                Value = memoryUsage
            });
        }
    }

    /// <summary>逐个订阅者调用快照事件，订阅者异常不会终止采集循环。</summary>
    private void RaiseSnapshotUpdated(SensorSnapshot snapshot)
    {
        var handlers = SnapshotUpdated;
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<SensorSnapshot>)handler)(snapshot); }
            catch (Exception ex) { LastError = "快照订阅者异常：" + ex.Message; }
        }
    }

    /// <summary>逐个订阅者调用阈值告警事件，订阅者异常不会终止采集循环。</summary>
    private void RaiseThresholdExceeded(ThresholdAlert alert)
    {
        var handlers = ThresholdExceeded;
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<ThresholdAlert>)handler)(alert); }
            catch (Exception ex) { LastError = "阈值告警订阅者异常：" + ex.Message; }
        }
    }

    private static void AddSensors(List<SensorReading> target, string hardwareType, IEnumerable<ISensor> sensors)
    {
        foreach (var sensor in sensors)
        {
            if (sensor.Value is not { } value || double.IsNaN(value) || double.IsInfinity(value)) continue;
            var kind = MapKind(sensor.SensorType);
            target.Add(new SensorReading
            {
                Identifier = sensor.Identifier?.ToString() ?? Guid.NewGuid().ToString("N"),
                Name = sensor.Name ?? string.Empty,
                HardwareName = BuildHardwareName(hardwareType, sensor),
                Kind = kind,
                Unit = MapUnit(sensor.SensorType),
                Value = value
            });
        }
    }

    private static string BuildHardwareName(string hardwareType, ISensor sensor)
    {
        var identifier = sensor.Identifier?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(identifier)) return hardwareType;
        // 形如 /intelcpu/0/temperature/3 → Intel CPU
        if (identifier.Contains("cpu", StringComparison.OrdinalIgnoreCase)) return "CPU";
        if (identifier.Contains("gpu", StringComparison.OrdinalIgnoreCase)) return "GPU " + hardwareType;
        if (identifier.Contains("hdd", StringComparison.OrdinalIgnoreCase)
            || identifier.Contains("storage", StringComparison.OrdinalIgnoreCase)) return "Storage";
        if (identifier.Contains("ram", StringComparison.OrdinalIgnoreCase)) return "Memory";
        return hardwareType;
    }

    private static SensorKind MapKind(LibreHardwareMonitor.Hardware.SensorType type) => type switch
    {
        LibreHardwareMonitor.Hardware.SensorType.Temperature => SensorKind.Temperature,
        LibreHardwareMonitor.Hardware.SensorType.Fan => SensorKind.Fan,
        LibreHardwareMonitor.Hardware.SensorType.Voltage => SensorKind.Voltage,
        LibreHardwareMonitor.Hardware.SensorType.Load => SensorKind.Load,
        LibreHardwareMonitor.Hardware.SensorType.Clock => SensorKind.Clock,
        LibreHardwareMonitor.Hardware.SensorType.Power => SensorKind.Power,
        _ => SensorKind.Other
    };

    private static string MapUnit(LibreHardwareMonitor.Hardware.SensorType type) => type switch
    {
        LibreHardwareMonitor.Hardware.SensorType.Temperature => "°C",
        LibreHardwareMonitor.Hardware.SensorType.Fan => "RPM",
        LibreHardwareMonitor.Hardware.SensorType.Voltage => "V",
        LibreHardwareMonitor.Hardware.SensorType.Load => "%",
        LibreHardwareMonitor.Hardware.SensorType.Clock => "MHz",
        LibreHardwareMonitor.Hardware.SensorType.Power => "W",
        LibreHardwareMonitor.Hardware.SensorType.Throughput => "B/s",
        _ => string.Empty
    };

    private void CheckThresholds(IReadOnlyList<SensorReading> readings, DateTime now)
    {
        IReadOnlyList<SensorThreshold> thresholds;
        lock (_sync) thresholds = _thresholds;
        if (thresholds.Count == 0) return;

        foreach (var threshold in thresholds.Where(t => t.Enabled))
        {
            foreach (var reading in readings)
            {
                if (threshold.Kind != reading.Kind) continue;
                if (!string.IsNullOrWhiteSpace(threshold.SensorIdentifier)
                    && !string.Equals(threshold.SensorIdentifier, reading.Identifier, StringComparison.OrdinalIgnoreCase)) continue;
                if (reading.Value <= threshold.MaxValue) continue;

                var key = threshold.SensorIdentifier + "|" + reading.Identifier;
                if (_lastAlert.TryGetValue(key, out var last) && now - last < _alertCooldown) continue;
                _lastAlert[key] = now;
                RaiseThresholdExceeded(new ThresholdAlert { Reading = reading, Threshold = threshold.MaxValue, RaisedAt = now });
            }
        }
    }

    public void Dispose()
    {
        Stop();
        Volatile.Write(ref _latest, Array.Empty<SensorReading>());
    }
}

/// <summary>
/// 传感器读数环形缓冲：为每个传感器保存最近 N 个采样点，供实时折线图绘制。
/// </summary>
public sealed class SensorHistory
{
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<double>> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    public SensorHistory(int capacity = 120) => _capacity = Math.Max(10, capacity);

    public void Add(IEnumerable<SensorReading> readings)
    {
        lock (_sync)
        {
            foreach (var reading in readings)
            {
                if (!_series.TryGetValue(reading.Identifier, out var queue))
                {
                    queue = new Queue<double>(_capacity);
                    _series[reading.Identifier] = queue;
                    _labels[reading.Identifier] = reading.DisplayName;
                }
                queue.Enqueue(reading.Value);
                while (queue.Count > _capacity) queue.Dequeue();
            }
        }
    }

    /// <summary>取指定传感器的历史曲线。</summary>
    public IReadOnlyList<double> GetSeries(string identifier)
    {
        lock (_sync)
            return _series.TryGetValue(identifier, out var queue) ? queue.ToArray() : Array.Empty<double>();
    }

    public IReadOnlyList<string> Identifiers
    {
        get { lock (_sync) return _series.Keys.ToArray(); }
    }

    public string GetLabel(string identifier)
    {
        lock (_sync) return _labels.TryGetValue(identifier, out var label) ? label : identifier;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _series.Clear();
            _labels.Clear();
        }
    }
}
