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

    /// <summary>在锁内修改设置并立即落盘，避免多页面并发写导致配置丢失。</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_sync)
        {
            change(Current);
            JsonStorage.Save(AppPaths.SettingsFile, Current);
        }
    }

    public void Reload()
    {
        lock (_sync)
        {
            Current = JsonStorage.Load<AppSettings>(AppPaths.SettingsFile);
        }
    }
}
