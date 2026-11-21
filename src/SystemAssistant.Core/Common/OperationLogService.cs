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
        // 日志是辅助功能：磁盘满、文件被独占、目录无写权限等持久化失败
        // 绝不能冒泡到业务操作（如"服务已启动但日志写失败"被误判为整体失败）。
        lock (_sync)
        {
            try
            {
                JsonStorage.AppendLine(_path, entry);
            }
            catch (Exception)
            {
                // 日志写入失败时静默降级；主业务结果不受影响。
            }
        }
        // 订阅者同样不能影响业务；UI 订阅方需自行切回 UI 线程。
        try { Changed?.Invoke(); }
        catch (Exception) { }
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

    /// <summary>清空日志。与 Log() 一致做双重异常隔离：磁盘满 / 文件被独占时不破坏主业务。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            try { File.WriteAllText(_path, string.Empty); }
            catch (Exception) { /* 清空失败不影响调用方，日志保留到下次重试 */ }
        }
        // 逐个订阅者保护：单个 UI 订阅者异常不能让清空操作整体抛错。
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception) { }
        }
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
