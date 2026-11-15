using System.Text;
using System.Text.Encodings.Web;

namespace SystemAssistant.Utils.IO;

/// <summary>报告导出的通用辅助：编码、HTML 转义、安全写入。</summary>
public static class ExportHelper
{
    /// <summary>写入文本文件（UTF-8 BOM，方便 Windows 记事本直接打开中文）。</summary>
    public static void WriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string HtmlEncode(string? value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

    /// <summary>根据扩展名生成带时间戳的报告文件名。</summary>
    public static string BuildReportFileName(string prefix, string extension, DateTime timestamp)
        => $"{prefix}_{timestamp:yyyyMMdd_HHmmss}.{extension.TrimStart('.')}";

    /// <summary>防止文件名包含非法字符。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString();
    }
}
