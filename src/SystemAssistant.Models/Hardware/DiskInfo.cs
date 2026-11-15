namespace SystemAssistant.Models.Hardware;

/// <summary>磁盘健康状态。</summary>
public enum DiskHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Failed = 3
}

/// <summary>磁盘介质类型。</summary>
public enum DiskMediaType
{
    Unknown = 0,
    HardDiskDrive = 1,
    SolidStateDrive = 2,
    Removable = 3
}

/// <summary>逻辑分区信息。</summary>
public sealed class PartitionInfo
{
    public string DriveLetter { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

/// <summary>SMART 属性原始项。</summary>
public sealed class SmartAttributeItem
{
    public byte Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int CurrentValue { get; init; }
    public int WorstValue { get; init; }
    public int Threshold { get; init; }
    public long RawValue { get; init; }
    public bool IsWarning { get; init; }
}

/// <summary>SMART 汇总信息。</summary>
public sealed class SmartInfo
{
    public bool IsAvailable { get; init; }
    public DiskHealthStatus Health { get; init; } = DiskHealthStatus.Unknown;
    public string Summary { get; init; } = string.Empty;
    public double? TemperatureCelsius { get; init; }
    public int? PowerOnHours { get; init; }
    public int? PowerCycleCount { get; init; }
    public IReadOnlyList<SmartAttributeItem> Attributes { get; init; } = Array.Empty<SmartAttributeItem>();
}

/// <summary>物理磁盘信息。</summary>
public sealed class DiskInfo
{
    public int DiskIndex { get; init; }
    public string Model { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string InterfaceType { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public DiskMediaType MediaType { get; init; } = DiskMediaType.Unknown;
    public string MediaTypeText => MediaType switch
    {
        DiskMediaType.SolidStateDrive => "SSD",
        DiskMediaType.HardDiskDrive => "HDD",
        DiskMediaType.Removable => "可移动磁盘",
        _ => "未知"
    };

    public DiskHealthStatus Health { get; init; } = DiskHealthStatus.Unknown;
    public SmartInfo Smart { get; init; } = new();
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = Array.Empty<PartitionInfo>();
}
