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
