using System.Collections.Concurrent;
using System.Diagnostics;
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
    private Task? _initTask;          // LHM 并行初始化任务，Stop 时必须 Join 防止句柄泄漏
    private bool _stopping;           // Stop 进行中：拒绝 Start，避免双 worker
    private volatile bool _disposed;  // Dispose 后不可复活
    private IReadOnlyList<SensorThreshold> _thresholds = Array.Empty<SensorThreshold>();
    private IReadOnlyList<SensorReading> _latest = Array.Empty<SensorReading>();
    private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(2);

    // LHM 不可用时的降级读数使用固定标识，保证曲线连续、LatestReadings 恒含 CPU / 内存两项
    private const string NativeCpuIdentifier = "native/cpu/load";
    private const string NativeMemoryIdentifier = "native/memory/load";

    // GetSystemTimes 首次 / 偶发失败时沿用上一次有效值，避免快照出现空白（仅后台工作线程读写）
    private double _lastCpuUsage;
    private bool _hasCpuSample;

    // CPU 占用滑动窗口：Windows 的 CPU 时间按 ~15.6ms tick 计量，250ms 短窗口会有
    // 明显量化抖动（实测 5%~27%）。对最近 4 帧取中位数可显著降低噪声，同时保留实时性
    // （4 帧 × 250ms = 1 秒窗口）。仅后台工作线程读写。
    private readonly Queue<double> _cpuWindow = new();
    private const int CpuWindowSize = 4;

    // 实时监控诊断：单调递增序号、最近一次成功采集的单调时钟、采集耗时
    private long _sequence;
    private long _lastCollectTick;
    private double _lastCostMs;

    public event Action<SensorSnapshot>? SnapshotUpdated;
    public event Action<ThresholdAlert>? ThresholdExceeded;

    public bool IsRunning => !_disposed && !_stopping && _worker is { IsCompleted: false };

    /// <summary>实时监控刷新间隔，默认 1 秒（真实时）。下限 250ms，避免过密采样拖垮系统。</summary>
    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>最近一次采集到的全部传感器读数（只读快照，线程安全）。</summary>
    public IReadOnlyList<SensorReading> LatestReadings => Volatile.Read(ref _latest);

    /// <summary>最近一次采集的诊断信息，供界面判断数据是否新鲜、是否降级。</summary>
    public (long Sequence, double CostMs, DateTime CollectedAt, string Diagnostic) LastDiagnostic
    {
        get
        {
            var seq = Interlocked.Read(ref _sequence);
            return (seq, Volatile.Read(ref _lastCostMs),
                new DateTime(Interlocked.Read(ref _lastCollectTick), DateTimeKind.Utc).ToLocalTime(),
                LastError);
        }
    }

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
            // _disposed / _stopping 必须在这里检查：Stop 持锁完成 Join 前 Start 会阻塞在锁上，
            // 拿到锁时 _stopping 已复位、worker 已终止，因此不会出现双 worker。
            if (_disposed || _stopping || _worker is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => RunLoopAsync(token), token);
        }
    }

    /// <summary>实时监控间隔钳制：250ms~60s。250ms 是普通权限下 Win32/LHM 采样不失真的下限。</summary>
    internal static int ClampInterval(int ms) => Math.Clamp(ms, 250, 60_000);

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        // 锁内只做状态切换与字段摘取，绝不持锁 Wait：
        // worker 的 finally 里需要 lock(_sync) 清理 _initTask，若 Stop 持锁等待 worker，
        // 就形成自锁——worker 拿不到锁无法结束，Wait 必然超时 3 秒（每次停止/关闭都卡顿）。
        // _stopping 在锁外等待期间保持 true，Start 在锁内检查它会被拒绝，因此仍无双 worker。
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
            cts?.Cancel();
        }
        try
        {
            try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            // 不等待 initTask：OpenComputer 在 Open() 返回后会检查取消令牌并自行
            // ReleaseComputer，因此 Cancel 后它会自己清理，Stop 无需阻塞数秒等它。
            // 之前 Wait(initTask) 导致无权限机器上关闭窗口卡 2~3 秒（Open 无法被取消中断）。
            cts?.Dispose();
            CloseComputer();
        }
        finally
        {
            lock (_sync) _stopping = false;
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            // 预热 GetSystemTimes 基准：首次调用只记录采样、返回 null。
            // 刻意不采用预热的返回值：若预热与首帧恰好跨过一个系统 tick(~15.6ms)，
            // 该值是基于亚毫秒间隔算出的 0%/100% 失真值，采用它会污染首帧并阻止 WMI 回落。
            _ = WindowsNativeInfo.GetCpuUsagePercent("sensor-monitor");

            // 关键架构：LHM 初始化（Open + 首次 Update）在无管理员权限时可阻塞 2~4 秒。
            // 把它放到独立任务里并行执行，主循环用 Win32 基础指标（CPU / 内存）持续按刷新
            // 间隔推送实时快照；LHM 就绪后 _computer 被赋值，后续帧自动并入温度 / 风扇等读数。
            // 这样整个初始化期间都有连续、无空窗的实时数据，而不是卡住几秒。
            var initTask = Task.Run(() =>
            {
                try { OpenComputer(token); }
                catch (Exception ex) { LastError = "传感器初始化受限：" + ex.Message; }
            }, token);
            lock (_sync) _initTask = initTask;

            // 立即推第一帧（不等定时器），用户点「开始采集」后马上有数据
            CollectOnce(basicOnly: true);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ClampInterval(RefreshIntervalMs)));
            while (await timer.WaitForNextTickAsync(token))
            {
                // 初始化未完成前用 basicOnly，避免读到未就绪的 LHM 造成空集合
                CollectOnce(basicOnly: !initTask.IsCompleted);
                if (Math.Abs(timer.Period.TotalMilliseconds - ClampInterval(RefreshIntervalMs)) > 1)
                {
                    timer.Period = TimeSpan.FromMilliseconds(ClampInterval(RefreshIntervalMs));
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
            lock (_sync) _initTask = null;
            CloseComputer();
        }
    }

    private void OpenComputer(CancellationToken token = default)
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
            // Open() 可能阻塞数秒；期间用户可能已 Stop。若已取消，刚打开的 Computer
            // 绝不能登记到字段（否则 worker 已退出、无人再释放，造成 Ring0 句柄泄漏）。
            if (token.IsCancellationRequested || _disposed)
            {
                ReleaseComputer(computer);
                computer = null;
                return;
            }
            lock (_sync)
            {
                if (token.IsCancellationRequested || _disposed)
                {
                    ReleaseComputer(computer);
                    computer = null;
                    return;
                }
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
    private void CollectOnce(bool basicOnly = false)
    {
        // 用单调时钟计量采集耗时与数据新鲜度；DateTime.Now 会受系统时间调整影响。
        var collectStart = Stopwatch.GetTimestamp();
        Computer? computer;
        lock (_sync) computer = basicOnly ? null : _computer;

        var readings = new List<SensorReading>();
        string? degradedError = null;

        if (basicOnly)
        {
            degradedError = "正在初始化硬件传感器，当前仅显示 Win32 基础指标（CPU / 内存）";
        }
        else if (computer is not null)
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
        // 滑动窗口中位数：抑制短窗口量化抖动，避免占用率忽高忽低。
        _cpuWindow.Enqueue(Math.Clamp(_lastCpuUsage, 0, 100));
        while (_cpuWindow.Count > CpuWindowSize) _cpuWindow.Dequeue();
        var cpuUsage = Median(_cpuWindow);

        // 单次 Win32 调用取到内存全部指标，保证 used / total / 百分比来自同一时刻，
        // 避免分多次读取导致三项数据互相矛盾（准确性关键）。
        var (memTotal, memAvailable, _, _) = WindowsNativeInfo.GetMemoryStatus();
        var memoryUsage = memTotal > 0
            ? Math.Clamp((1 - memAvailable * 1.0 / memTotal) * 100, 0, 100)
            : 0;
        var usedMemoryBytes = memTotal > 0 && memAvailable >= 0 && memAvailable <= memTotal
            ? memTotal - memAvailable : (long?)null;

        // 缺少 LHM 的 CPU / 内存负载读数时补上 Win32 原生读数，
        // 保证 LatestReadings 至少有 CPU 与内存两条，且曲线标识稳定连续。
        EnsureNativeLoadReadings(readings, cpuUsage, memoryUsage);

        // 异常值过滤：传感器偶发返回物理上不可能的值（负温度、超阈值读数、离谱频率），
        // 直接展示会造成曲线炸裂与用户误判。这里按类别丢弃明显非法值。
        var beforeFilter = readings.Count;
        var validReadings = readings.Where(IsPhysicallyPlausible).ToList();
        var filteredCount = beforeFilter - validReadings.Count;
        if (filteredCount > 0)
            degradedError = (degradedError is null ? string.Empty : degradedError + "；")
                + $"已过滤 {filteredCount} 条异常读数";

        // 从本轮已过滤的读数提取实时 CPU 温度 / 功耗 / 频率（不额外查 WMI，保证同一时刻）
        static double? Pick(IReadOnlyList<SensorReading> rs, SensorKind kind, string namePart)
        {
            var hit = rs.FirstOrDefault(r => r.Kind == kind
                && r.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase));
            return hit?.Value;
        }
        var cpuTemp = Pick(validReadings, SensorKind.Temperature, "Package")
                      ?? Pick(validReadings, SensorKind.Temperature, "Core")
                      ?? validReadings.FirstOrDefault(r => r.Kind == SensorKind.Temperature)?.Value;
        var cpuPower = Pick(validReadings, SensorKind.Power, "Package")
                       ?? validReadings.FirstOrDefault(r => r.Kind == SensorKind.Power)?.Value;
        var cpuClock = validReadings.FirstOrDefault(r => r.Kind == SensorKind.Clock
            && r.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))?.Value;

        var now = DateTime.Now;
        foreach (var reading in validReadings) reading.Timestamp = now;
        // LatestReadings 与事件载荷共用同一份数组快照：早先直接暴露可变 List，
        // 界面在 Stop/Dispose 之后可能读到空集合，导致 CPU 温度等派生字段恒为 N/A。
        var snapshotReadings = validReadings.ToArray();
        Volatile.Write(ref _latest, snapshotReadings);
        History.Add(snapshotReadings);

        // 失败原因只记录、不阻断快照推送
        LastError = degradedError ?? string.Empty;

        var costTicks = Stopwatch.GetTimestamp() - collectStart;
        double costMs = costTicks * (1000.0 / Stopwatch.Frequency);
        Volatile.Write(ref _lastCostMs, costMs);
        Interlocked.Exchange(ref _lastCollectTick, DateTime.UtcNow.Ticks);
        var sequence = Interlocked.Increment(ref _sequence);

        var snapshot = new SensorSnapshot
        {
            Timestamp = now,
            Readings = snapshotReadings,
            CpuUsagePercent = cpuUsage,
            MemoryUsagePercent = memoryUsage,
            CpuTemperature = cpuTemp,
            CpuPower = cpuPower,
            CpuClockMhz = cpuClock,
            UsedMemoryBytes = usedMemoryBytes,
            TotalMemoryBytes = memTotal > 0 ? memTotal : null,
            Sequence = sequence,
            CollectionCostMs = costMs,
            Diagnostic = LastError,
            // 采集耗时超过设定间隔说明采样跟不上，标记让界面提示用户调大间隔。
            // 首帧（Sequence==1）是冷启动 JIT 开销，已有"正在初始化"诊断，不标过期。
            IsStale = sequence > 1 && costMs > ClampInterval(RefreshIntervalMs)
        };
        RaiseSnapshotUpdated(snapshot);
        CheckThresholds(validReadings, now);
    }

    /// <summary>取序列中位数：比平均值更能抵抗偶发尖峰，用于平滑 CPU 量化抖动。</summary>
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// 物理合理性校验：丢掉传感器偶发的越界读数（负绝对温度、超过 300°C、
    /// 占用率超出 0~100、负频率 / 负风扇转速等），保证曲线与展示不被异常值污染。
    /// </summary>
    private static bool IsPhysicallyPlausible(SensorReading r)
    {
        var v = r.Value;
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        return r.Kind switch
        {
            SensorKind.Temperature => v >= -60 && v <= 300,
            SensorKind.Load => v >= 0 && v <= 100,
            SensorKind.Clock => v >= 0 && v <= 20_000,
            SensorKind.Fan => v >= 0 && v <= 30_000,
            // ATX 电源有合规的负电压轨（-12V / -5V），不能一律按负值过滤掉
            SensorKind.Voltage => v >= -20 && v <= 100,
            SensorKind.Power => v >= 0 && v <= 2000,
            _ => true
        };
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

        var index = 0;
        foreach (var threshold in thresholds)
        {
            var thresholdIndex = index++;
            if (!threshold.Enabled) continue;
            foreach (var reading in readings)
            {
                if (threshold.Kind != reading.Kind) continue;
                if (!string.IsNullOrWhiteSpace(threshold.SensorIdentifier)
                    && !string.Equals(threshold.SensorIdentifier, reading.Identifier, StringComparison.OrdinalIgnoreCase)) continue;
                if (reading.Value <= threshold.MaxValue) continue;

                // 冷却键必须含阈值身份：同一传感器被多条阈值覆盖时，
                // 只按 sensor 标识做键会让先触发的阈值把其余阈值在冷却期内全部抑制。
                var key = $"{thresholdIndex}|{(int)threshold.Kind}|{threshold.MaxValue:G}|"
                          + $"{threshold.SensorIdentifier}|{reading.Identifier}";
                if (_lastAlert.TryGetValue(key, out var last) && now - last < _alertCooldown) continue;
                _lastAlert[key] = now;
                RaiseThresholdExceeded(new ThresholdAlert { Reading = reading, Threshold = threshold.MaxValue, RaisedAt = now });
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
