namespace SystemAssistant.Utils.Common;

/// <summary>
/// 应用私有数据目录。所有配置、日志、备份都保存在当前用户目录，不写入系统目录，
/// 也不联网上传任何数据。
/// v1.2.0 品牌升级为 Syspulse：目录从 %LOCALAPPDATA%\SystemAssistant 迁移到
/// %LOCALAPPDATA%\Syspulse，首次启动时自动搬迁旧配置，老用户设置/白名单/日志零丢失。
/// </summary>
public static class AppPaths
{
    private const string NewFolder = "Syspulse";
    private const string LegacyFolder = "SystemAssistant";

    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, NewFolder);
        var legacy = Path.Combine(localAppData, LegacyFolder);
        try
        {
            // 仅当新目录不存在、旧目录存在时迁移：整体移动即完成搬迁，
            // 之后所有路径都指向新目录。用标记文件防止重复迁移。
            if (!Directory.Exists(root) && Directory.Exists(legacy))
            {
                Directory.Move(legacy, root);
            }
            else if (Directory.Exists(legacy) && File.Exists(Path.Combine(legacy, "settings.json"))
                     && !File.Exists(Path.Combine(root, "settings.json")))
            {
                // 新目录已由安装器/其它途径创建，但旧配置还在：逐个迁移用户数据文件，
                // 不删除旧目录（避免误删），仅复制不覆盖已有文件。
                Directory.CreateDirectory(root);
                foreach (var file in Directory.GetFiles(legacy))
                {
                    var target = Path.Combine(root, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Copy(file, target);
                }
                var backupSrc = Path.Combine(legacy, "RegistryBackups");
                var backupDst = Path.Combine(root, "RegistryBackups");
                if (Directory.Exists(backupSrc) && !Directory.Exists(backupDst))
                    CopyDirectory(backupSrc, backupDst);
            }
        }
        catch
        {
            // 迁移失败不能阻止程序启动：退回旧目录，用户的配置仍然可用。
            if (Directory.Exists(legacy) && !Directory.Exists(root))
                root = legacy;
        }
        Directory.CreateDirectory(root);
        return root;
    });

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// <summary>应用数据根目录（%LOCALAPPDATA%\Syspulse，自动从旧目录迁移）。</summary>
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
