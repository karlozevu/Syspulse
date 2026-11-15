using System.Text;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.IO;

namespace SystemAssistant.Core.Common;

/// <summary>
/// 操作日志服务。JSONL 追加写，包含时间、操作内容、是否成功。
/// 日志只保存在本机用户目录，不会联网上传。
/// </summary>
public sealed class OperationLogService : IOperationLogService
{
    private readonly string _path;
    private readonly object _sync = new();

    public OperationLogService() : this(AppPaths.OperationLogFile) { }

    public OperationLogService(string path) => _path = path;

    /// <summary>日志变更事件，便于界面即时刷新。</summary>
    public event Action? Changed;

    public void Log(OperationType type, string action, string detail, bool success, string? errorMessage = null)
    {
        var entry = new OperationLogEntry
        {
            Timestamp = DateTime.Now,
            Type = type,
            Action = action,
            Detail = detail,
            Success = success,
            ErrorMessage = errorMessage
        };
        lock (_sync)
        {
            JsonStorage.AppendLine(_path, entry);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<OperationLogEntry> GetAll() => JsonStorage.ReadLines<OperationLogEntry>(_path);

    /// <summary>导出为带表头的 CSV（Excel 可直接打开）。</summary>
    public void ExportTo(string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine("时间,类型,操作,详情,是否成功,错误信息");
        foreach (var entry in GetAll())
        {
            builder.AppendLine(string.Join(",",
                Csv(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(entry.Type.ToString()),
                Csv(entry.Action),
                Csv(entry.Detail),
                Csv(entry.Success ? "成功" : "失败"),
                Csv(entry.ErrorMessage ?? string.Empty)));
        }
        ExportHelper.WriteText(path, builder.ToString());
    }

    public void Clear()
    {
        lock (_sync)
        {
            File.WriteAllText(_path, string.Empty);
        }
        Changed?.Invoke();
    }

    private static string Csv(string value)
    {
        // 防止 CSV 公式注入：Excel / WPS 会把以 = + - @ 开头的单元格当成公式执行。
        // 前置单引号强制按文本处理（前面的空白字符也要跳过再判断）。
        var significant = value.TrimStart(' ', '\t', '\r', '\n');
        if (significant.Length > 0 && significant[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
