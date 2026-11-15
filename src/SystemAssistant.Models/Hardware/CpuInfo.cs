namespace SystemAssistant.Models.Hardware;

/// <summary>CPU 静态信息与实时指标。实时指标由传感器采集器持续更新。</summary>
public sealed class CpuInfo
{
    public string Name { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Socket { get; init; } = string.Empty;
    public int PhysicalCores { get; init; }
    public int LogicalProcessors { get; init; }

    /// <summary>基础频率（MHz）。</summary>
    public double BaseClockMHz { get; init; }

    /// <summary>最大睿频（MHz，WMI 通常不提供，由传感器估算或显示为 N/A）。</summary>
    public double MaxTurboMHz { get; init; }

    /// <summary>L1 指令 / 数据缓存（KB）。</summary>
    public long L1CacheKB { get; init; }

    public long L2CacheKB { get; init; }
    public long L3CacheKB { get; init; }

    /// <summary>当前总占用率（0~100）。</summary>
    public double UsagePercent { get; set; }

    /// <summary>当前实时频率（MHz）。</summary>
    public double CurrentClockMHz { get; set; }

    /// <summary>当前温度（摄氏度），无传感器时为 null。</summary>
    public double? TemperatureCelsius { get; set; }

    /// <summary>当前封装功耗（W），无传感器时为 null。</summary>
    public double? PackagePowerWatt { get; set; }
}
