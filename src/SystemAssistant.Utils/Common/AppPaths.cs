namespace SystemAssistant.Utils.Common;

/// <summary>
/// 应用私有数据目录。所有配置、日志、备份都保存在当前用户目录，不写入系统目录，
/// 也不联网上传任何数据。
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "SystemAssistant");
        Directory.CreateDirectory(root);
        return root;
    });

    /// <summary>应用数据根目录（%LOCALAPPDATA%\SystemAssistant）。</summary>
    public static string Root => RootLazy.Value;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string OperationLogFile => Path.Combine(Root, "operations.jsonl");
    public static string DiagnosticsLogFile => Path.Combine(Root, "diagnostics.log");
    public static string AlertHistoryFile => Path.Combine(Root, "alerts.jsonl");
    public static string BackupDirectory => Path.Combine(Root, "RegistryBackups");

    /// <summary>默认导出目录（用户桌面）。</summary>
    public static string DefaultExportDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>确保目录存在并返回其路径。</summary>
    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
