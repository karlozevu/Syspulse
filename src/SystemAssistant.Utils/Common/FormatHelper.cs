using System.Globalization;

namespace SystemAssistant.Utils.Common;

/// <summary>展示层无关的格式化工具。</summary>
public static class FormatHelper
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>按 1024 进制格式化字节数。</summary>
    public static string FormatBytes(long bytes, int decimals = 1)
    {
        if (bytes <= 0) return "0 B";
        decimals = Math.Clamp(decimals, 0, 15);
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /// <summary>速率格式化（字节/秒）。NaN / 无穷大 / 负值一律显示 N/A。</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0)
            return "N/A";
        // (long) 对超出 Int64 的有限值会 unchecked 溢出成负数，"953 EB" 之类会显示成 0 B/s。
        var clamped = Math.Min(bytesPerSecond, long.MaxValue);
        return FormatBytes((long)clamped) + "/s";
    }

    /// <summary>
    /// 把 WMI 的 CIM_DATETIME（yyyymmddHHMMSS.xxxxxx±xxx）转换成本地时间字符串。
    /// 尾部的 ±xxx 是与 UTC 相差的分钟数，必须纳入换算，否则跨时区 / 夏令时展示会偏差。
    /// </summary>
    public static string FormatCimDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var dotIndex = raw.IndexOf('.');
        var datePart = dotIndex >= 0 ? raw[..dotIndex] : raw;
        if (datePart.Length < 14) return raw;
        if (!DateTime.TryParseExact(datePart[..14], "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return raw;

        if (dotIndex >= 0)
        {
            var tail = raw[(dotIndex + 1)..];
            var signIndex = tail.LastIndexOfAny(new[] { '+', '-' });
            if (signIndex >= 0 && signIndex + 1 < tail.Length
                && int.TryParse(tail[(signIndex + 1)..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var offsetMinutes))
            {
                if (tail[signIndex] == '-') offsetMinutes = -offsetMinutes;
                try
                {
                    // 时间字段是「该偏移下的本地墙钟时间」，先固定偏移再换算到本机本地时间
                    var withOffset = new DateTimeOffset(dt, TimeSpan.FromMinutes(offsetMinutes));
                    return withOffset.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 偏移非法（超出 ±14 小时等）：回退到直接展示时间字段
                }
            }
        }
        return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>把 WMI 的 yyyymmdd 日期字符串格式化为 yyyy-MM-dd。</summary>
    public static string FormatCimDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return raw ?? string.Empty;
        return $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}";
    }

    public static string FormatPercent(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "N/A" : $"{value:F1} %";
}
