namespace SystemAssistant.Models.Hardware;

/// <summary>显卡信息，包含 WMI 静态信息与传感器实时指标。</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string VideoProcessor { get; init; } = string.Empty;
    public string Resolution { get; init; } = string.Empty;

    /// <summary>显存总量（字节）；0 表示无法可靠读取。</summary>
    public long VramTotalBytes { get; init; }

    /// <summary>
    /// 显存容量来源：Registry = 驱动注册表的 64 位精确值；
    /// WMI = Win32_VideoController.AdapterRAM（uint32，可能受 4 GB 上限截断）；
    /// Unknown = 两者都不可用。界面据此决定是否给出「仅供参考」提示。
    /// </summary>
    public string VramSource { get; init; } = "Unknown";

    /// <summary>已占用显存（字节），无传感器时为 null。</summary>
    public long? VramUsedBytes { get; set; }

    /// <summary>GPU 负载（0~100），无传感器时为 null。</summary>
    public double? LoadPercent { get; set; }

    /// <summary>核心温度（摄氏度），无传感器时为 null。</summary>
    public double? TemperatureCelsius { get; set; }
}
