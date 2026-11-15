using System.Collections.Concurrent;
using LibreHardwareMonitor.Hardware;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Hardware;

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
        try
        {
            var computer = new Computer
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
            lock (_sync) _computer = computer;
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            // 常见于未以管理员权限运行：部分主板 / 风扇传感器不可用
            LastError = "传感器初始化受限：" + ex.Message;
        }
    }

    private void CloseComputer()
    {
        try
        {
            lock (_sync)
            {
                _computer?.Close();
                _computer = null;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void CollectOnce()
    {
        Computer? computer;
        lock (_sync) computer = _computer;
        if (computer is null)
        {
            OpenComputer();
            lock (_sync) computer = _computer;
            if (computer is null) return;
        }

        var readings = new List<SensorReading>();
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
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = "传感器读取中断：" + ex.Message;
            return;
        }

        var now = DateTime.Now;
        foreach (var reading in readings) reading.Timestamp = now;
        Volatile.Write(ref _latest, readings);
        History.Add(readings);

        double? cpuUsage = null;
        double? memoryUsage = null;
        var cpuLoads = readings.Where(r => r.Kind == SensorKind.Load
            && r.Name.Contains("CPU Total", StringComparison.OrdinalIgnoreCase)).Select(r => r.Value).ToList();
        if (cpuLoads.Count > 0) cpuUsage = cpuLoads.Average();
        var memoryLoads = readings.Where(r => r.Kind == SensorKind.Load
            && r.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)).Select(r => r.Value).ToList();
        if (memoryLoads.Count > 0) memoryUsage = memoryLoads.Average();

        var snapshot = new SensorSnapshot
        {
            Timestamp = now,
            Readings = readings.ToArray(),
            CpuUsagePercent = cpuUsage,
            MemoryUsagePercent = memoryUsage
        };
        SnapshotUpdated?.Invoke(snapshot);
        CheckThresholds(readings, now);
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
                ThresholdExceeded?.Invoke(new ThresholdAlert { Reading = reading, Threshold = threshold.MaxValue, RaisedAt = now });
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
