namespace SystemAssistant.Models.Hardware;

/// <summary>物理内存条信息。</summary>
public sealed class MemoryModuleInfo
{
    public string Manufacturer { get; init; } = string.Empty;
    public string PartNumber { get; init; } = string.Empty;
    public long CapacityBytes { get; init; }
    public long SpeedMHz { get; init; }
    public string Slot { get; init; } = string.Empty;
}

/// <summary>内存总览与分页文件信息。</summary>
public sealed class MemoryInfo
{
    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public long AvailableBytes { get; init; }
    public double UsagePercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;

    /// <summary>已提交的页面文件使用量（字节）。</summary>
    public long PageFileUsedBytes { get; init; }

    /// <summary>页面文件总量（字节）。</summary>
    public long PageFileTotalBytes { get; init; }

    /// <summary>已安装内存条数量。</summary>
    public int ModuleCount { get; init; }

    /// <summary>内存通道描述，例如 "双通道 (2 x 16 GB)"。</summary>
    public string ChannelDescription { get; init; } = string.Empty;

    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = Array.Empty<MemoryModuleInfo>();
}
