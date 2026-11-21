using System.Text.Json.Serialization;

namespace SystemAssistant.Models.Health;

/// <summary>一条硬件阈值告警历史记录（落盘到 JSONL）。</summary>
public sealed class AlertHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SensorName { get; set; } = string.Empty;
    public string HardwareName { get; set; } = string.Empty;
    public SensorKind WatchKind { get; set; }
    public double Value { get; set; }
    public double Threshold { get; set; }
    public string Unit { get; set; } = string.Empty;

    /// <summary>展示用：超出阈值的幅度（绝对值）。</summary>
    [JsonIgnore]
    public double ExceededBy => Math.Max(0, Value - Threshold);
}

/// <summary>传感器类别（与硬件 SensorKind 对齐的独立副本，避免 Models 层交叉依赖）。</summary>
public enum SensorKind
{
    Temperature, Fan, Voltage, Load, Clock, Power, Other
}

/// <summary>健康体检的单个问题项。</summary>
public sealed class HealthIssue
{
    public required string Category { get; init; }

    /// <summary>本地化 key（UI 层用 L(key) 渲染，参数用 MessageArg 填充）。</summary>
    public string MessageKey { get; init; } = string.Empty;
    public string MessageArg { get; init; } = string.Empty;

    /// <summary>已本地化的消息（UI 层组装后回填，供报告导出使用）。</summary>
    public string Message { get; set; } = string.Empty;

    public HealthSeverity Severity { get; init; }
}

public enum HealthSeverity { Info, Warning, Critical }

/// <summary>一次系统健康体检的完整结果。</summary>
public sealed class HealthReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;

    /// <summary>健康评分 0~100，越高越健康。</summary>
    public int Score { get; init; } = 100;

    public IReadOnlyList<HealthIssue> Issues { get; init; } = Array.Empty<HealthIssue>();

    // 各项指标快照（体检时刻）
    public double CpuUsagePercent { get; init; }
    public double? CpuTemperature { get; init; }
    public double MemoryUsagePercent { get; init; }
    public double? MemoryAvailableGB { get; init; }
    public int DiskCount { get; init; }
    public int UnhealthyDiskCount { get; init; }
    public int BatteryWearPercent { get; init; }
    public bool IsBatteryPresent { get; init; }
    public int SensorCount { get; init; }

    /// <summary>按严重度汇总的问题数，供 UI 一行展示。</summary>
    public int WarningCount => Issues.Count(i => i.Severity == HealthSeverity.Warning);
    public int CriticalCount => Issues.Count(i => i.Severity == HealthSeverity.Critical);
}

/// <summary>一次网络采样点（实时速率）。</summary>
public sealed class NetworkSample
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public double DownloadBytesPerSecond { get; init; }
    public double UploadBytesPerSecond { get; init; }
}

/// <summary>网络接口静态信息 + 累计流量。</summary>
public sealed class NetworkInterfaceInfo
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long SpeedBitsPerSecond { get; init; }
    public long TotalReceivedBytes { get; init; }
    public long TotalSentBytes { get; init; }
    public bool IsActive { get; init; }
}
