namespace SystemAssistant.Models.Hardware;

/// <summary>操作系统概览信息。</summary>
public sealed class OperatingSystemInfo
{
    /// <summary>系统名称，例如 Windows 11 Pro。</summary>
    public string Caption { get; init; } = string.Empty;

    /// <summary>完整版本号，例如 10.0.22631。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>系统内部版本号（Build Number）。</summary>
    public string BuildNumber { get; init; } = string.Empty;

    /// <summary>系统架构（x64 / ARM64）。</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>系统安装时间（本地时间，格式 yyyy-MM-dd HH:mm:ss）。</summary>
    public string InstallDate { get; init; } = string.Empty;

    /// <summary>最近一次启动时间。</summary>
    public string LastBootTime { get; init; } = string.Empty;

    /// <summary>计算机名。</summary>
    public string ComputerName { get; init; } = string.Empty;

    /// <summary>当前登录用户。</summary>
    public string UserName { get; init; } = string.Empty;
}

/// <summary>主板信息。</summary>
public sealed class MotherboardInfo
{
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
}

/// <summary>BIOS 信息。</summary>
public sealed class BiosInfo
{
    public string Manufacturer { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
}

/// <summary>网卡基础信息。</summary>
public sealed class NetworkAdapterInfo
{
    public string Name { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public string IpAddresses { get; init; } = string.Empty;
    public string Speed { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
}

/// <summary>整机快照，用于概览页面与整机报告导出。</summary>
public sealed class SystemOverview
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public OperatingSystemInfo OperatingSystem { get; init; } = new();
    public MotherboardInfo Motherboard { get; init; } = new();
    public BiosInfo Bios { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = Array.Empty<NetworkAdapterInfo>();
    public BatteryInfo Battery { get; init; } = new();
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = Array.Empty<DisplayInfo>();
}
