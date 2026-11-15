namespace SystemAssistant.Models.Hardware;

/// <summary>显卡信息，包含 WMI 静态信息与传感器实时指标。</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string VideoProcessor { get; init; } = string.Empty;
    public string Resolution { get; init; } = string.Empty;

    /// <summary>显存总量（字节）。</summary>
    public long VramTotalBytes { get; init; }

    /// <summary>已占用显存（字节），无传感器时为 null。</summary>
    public long? VramUsedBytes { get; set; }

    /// <summary>GPU 负载（0~100），无传感器时为 null。</summary>
    public double? LoadPercent { get; set; }

    /// <summary>核心温度（摄氏度），无传感器时为 null。</summary>
    public double? TemperatureCelsius { get; set; }
}
