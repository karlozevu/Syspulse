namespace SystemAssistant.Utils.Common;

/// <summary>展示层无关的格式化工具。</summary>
public static class FormatHelper
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>按 1024 进制格式化字节数。</summary>
    public static string FormatBytes(long bytes, int decimals = 1)
    {
        if (bytes <= 0) return "0 B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /// <summary>速率格式化（字节/秒）。</summary>
    public static string FormatSpeed(double bytesPerSecond) => FormatBytes((long)bytesPerSecond) + "/s";

    /// <summary>把 WMI 的 CIM_DATETIME（yyyymmddHHMMSS.xxxxxx+xxx）转换成本地时间字符串。</summary>
    public static string FormatCimDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var datePart = raw.Split('.')[0];
        if (datePart.Length < 14) return raw;
        if (DateTime.TryParseExact(datePart, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
        return raw;
    }

    /// <summary>把 WMI 的 yyyymmdd 日期字符串格式化为 yyyy-MM-dd。</summary>
    public static string FormatCimDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return raw ?? string.Empty;
        return $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}";
    }

    public static string FormatPercent(double value) => $"{value:F1} %";
}
