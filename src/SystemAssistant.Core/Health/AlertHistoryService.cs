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
    // 所有读写/裁剪/清空统一用这一把实例锁：此前 Append/Trim 无锁、
    // Clear 用另一把静态锁，并发时旧快照会覆盖新追加导致"清空后告警复活"。
    private readonly object _sync = new();

    public AlertHistoryService()
    {
        _path = AppPaths.AlertHistoryFile;
    }

    public void Append(AlertHistoryEntry entry)
    {
        lock (_sync)
        {
            try
            {
                JsonStorage.AppendLine(_path, entry);
                TrimIfNeededLocked();
            }
            catch
            {
                // 历史落盘是辅助功能：失败不应影响告警主流程
            }
        }
    }

    public IReadOnlyList<AlertHistoryEntry> GetAll()
    {
        lock (_sync)
        {
            try { return JsonStorage.ReadLines<AlertHistoryEntry>(_path); }
            catch { return Array.Empty<AlertHistoryEntry>(); }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            try { File.WriteAllText(_path, string.Empty); }
            catch { /* 与 OperationLog.Clear 一致：静默降级 */ }
        }
    }

    /// <summary>在 _sync 锁内调用：超过上限时原子裁剪（临时文件 + 替换）。</summary>
    private void TrimIfNeededLocked()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var lines = File.ReadAllLines(_path);
            if (lines.Length <= MaxEntries) return;
            var kept = lines.TakeLast(MaxEntries).ToArray();
            var temp = _path + ".trim.tmp";
            File.WriteAllLines(temp, kept);
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* 裁剪失败可下次再试，不影响写入 */ }
    }
}
