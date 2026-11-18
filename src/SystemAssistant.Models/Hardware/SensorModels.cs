namespace SystemAssistant.Models.Hardware;

/// <summary>传感器类别。</summary>
public enum SensorKind
{
    Temperature,
    Fan,
    Voltage,
    Load,
    Clock,
    Power,
    Other
}

/// <summary>单个传感器的一次读数。</summary>
public sealed class SensorReading
{
    public string Identifier { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HardwareName { get; init; } = string.Empty;
    public SensorKind Kind { get; init; }
    public string Unit { get; init; } = string.Empty;
    public double Value { get; init; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>曲线图上的显示名（硬件 + 传感器名）。</summary>
    public string DisplayName => string.IsNullOrEmpty(HardwareName) ? Name : $"{HardwareName} / {Name}";
}

/// <summary>一次采集周期的完整快照。</summary>
public sealed class SensorSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public IReadOnlyList<SensorReading> Readings { get; init; } = Array.Empty<SensorReading>();
    public double? CpuUsagePercent { get; init; }
    public double? MemoryUsagePercent { get; init; }

    // 实时动态指标：均由本轮 readings / Win32 API 同一采集窗口提取，无需额外 WMI 查询
    /// <summary>当前 CPU 温度（°C），从传感器读数提取；无有效读数时为 null。</summary>
    public double? CpuTemperature { get; init; }
    /// <summary>当前 CPU 封装功耗（W）。</summary>
    public double? CpuPower { get; init; }
    /// <summary>当前 CPU 实时频率（MHz）。</summary>
    public double? CpuClockMhz { get; init; }
    /// <summary>已用物理内存字节数，与 MemoryUsagePercent 同一时刻取得。</summary>
    public long? UsedMemoryBytes { get; init; }
    /// <summary>物理内存总字节数。</summary>
    public long? TotalMemoryBytes { get; init; }

    /// <summary>单调递增的采集序号，用于判断界面拿到的是否为最新快照。</summary>
    public long Sequence { get; init; }

    /// <summary>本轮采集耗时（毫秒）。超过刷新间隔说明采样跟不上设定频率。</summary>
    public double CollectionCostMs { get; init; }

    /// <summary>本轮是否触发了降级（LHM 不可用 / 读数被过滤等）。非空即为需要提示用户的原因。</summary>
    public string Diagnostic { get; init; } = string.Empty;

    /// <summary>数据是否过期（采集循环已停止或卡顿超过 3 个周期）。过期时界面应标注而非当作当前值。</summary>
    public bool IsStale { get; init; }
}

/// <summary>阈值配置。超过阈值时由界面弹出提醒。</summary>
public sealed class SensorThreshold
{
    /// <summary>传感器唯一标识；为空表示按类别匹配全部。</summary>
    public string SensorIdentifier { get; init; } = string.Empty;

    public SensorKind Kind { get; init; } = SensorKind.Temperature;

    /// <summary>告警上限（按传感器单位）。</summary>
    public double MaxValue { get; init; } = 85;

    public bool Enabled { get; init; } = true;
}

/// <summary>阈值告警事件。</summary>
public sealed class ThresholdAlert
{
    public SensorReading Reading { get; init; } = new();
    public double Threshold { get; init; }
    public DateTime RaisedAt { get; init; } = DateTime.Now;
}
