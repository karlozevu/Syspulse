using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Config;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.Core.Common;

/// <summary>用户设置服务：读写 %LOCALAPPDATA%\SystemAssistant\settings.json。</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly object _sync = new();

    public SettingsService()
    {
        Current = JsonStorage.Load<AppSettings>(AppPaths.SettingsFile);
        if (string.IsNullOrWhiteSpace(Current.DefaultExportDirectory))
            Current.DefaultExportDirectory = AppPaths.DefaultExportDirectory;
        if (string.IsNullOrWhiteSpace(Current.RegistryBackupDirectory))
            Current.RegistryBackupDirectory = AppPaths.BackupDirectory;
    }

    public AppSettings Current { get; private set; }

    /// <summary>设置成功写回后触发（无参数），供 UI 层订阅同步。</summary>
    public event Action? Changed;

    /// <summary>在锁内修改设置并立即落盘，避免多页面并发写导致配置丢失。</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_sync)
        {
            change(Current);
            JsonStorage.Save(AppPaths.SettingsFile, Current);
        }
        // 在锁外触发，避免订阅者重入或阻塞其它页面的设置写入
        RaiseChanged();
    }

    public void Reload()
    {
        lock (_sync)
        {
            Current = JsonStorage.Load<AppSettings>(AppPaths.SettingsFile);
        }
        RaiseChanged();
    }

    /// <summary>逐个订阅者触发，订阅者异常不影响已落盘的设置。</summary>
    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { /* 订阅者异常不应破坏设置保存 / 重载 */ }
        }
    }
}
