namespace SystemAssistant.Models.Hardware;

/// <summary>显示器信息。</summary>
public sealed class DisplayInfo
{
    public string DeviceName { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshRateHz { get; init; }

    /// <summary>每像素位深（bpp）。</summary>
    public int BitsPerPixel { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>高级色彩 / HDR 是否开启；系统不支持时为 null。</summary>
    public bool? HdrEnabled { get; init; }

    public string ResolutionText => $"{Width} x {Height} @ {RefreshRateHz} Hz";
}
