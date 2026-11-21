using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Health;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.Core.Health;

/// <summary>
/// 硬件阈值告警历史：把每次告警追加到 JSONL，供 UI 查看。
/// 只保留最近 N 条，防止无限增长；写盘异常隔离，绝不影响告警弹窗主流程。
/// </summary>
public sealed class AlertHistoryService : IAlertHistoryService
{
    private const int MaxEntries = 500;
    private readonly string _path;

    public AlertHistoryService()
    {
        _path = AppPaths.AlertHistoryFile;
    }

    public void Append(AlertHistoryEntry entry)
    {
        try
        {
            JsonStorage.AppendLine(_path, entry);
            TrimIfNeeded();
        }
        catch
        {
            // 历史落盘是辅助功能：失败不应影响告警主流程
        }
    }

    public IReadOnlyList<AlertHistoryEntry> GetAll()
        => JsonStorage.ReadLines<AlertHistoryEntry>(_path);

    public void Clear()
    {
        try
        {
            lock (Sync) File.WriteAllText(_path, string.Empty);
        }
        catch { /* 与 OperationLog.Clear 一致：静默降级 */ }
    }

    private static readonly object Sync = new();

    private void TrimIfNeeded()
    {
        try
        {
            var lines = File.ReadAllLines(_path);
            if (lines.Length <= MaxEntries) return;
            var kept = lines.TakeLast(MaxEntries).ToArray();
            File.WriteAllLines(_path, kept);
        }
        catch { /* 裁剪失败可下次再试，不影响写入 */ }
    }
}
