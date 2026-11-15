using Microsoft.Win32;
using SystemAssistant.Models.Cleaning;

namespace SystemAssistant.Core.Cleaning;

/// <summary>
/// 注册表「保守扫描」提供者。
/// 只报告有明确失效证据的项（安装目录不存在、图标 / 命令指向的文件不存在、ProgID 缺失），
/// 绝不做启发式批量删除；所有结果一律标记为高风险且默认不勾选。
/// 真正的删除由 <see cref="CleanService"/> 在注册表备份成功后执行。
/// </summary>
internal sealed class RegistryScanProvider : IScanProvider
{
    private const int MaxResults = 120;

    public Task<IReadOnlyList<ScanItem>> ScanAsync(ScanOptions options, WhitelistMatcher whitelist,
        IProgress<string>? status, CancellationToken token)
    {
        if (!options.ScanRegistryResidue)
            return Task.FromResult<IReadOnlyList<ScanItem>>(Array.Empty<ScanItem>());

        var items = new List<ScanItem>();
        status?.Report("正在扫描注册表：无效的卸载残留");
        ScanUninstallResidues(items, whitelist, token);

        if (items.Count < MaxResults)
        {
            status?.Report("正在扫描注册表：无效的 App Paths");
            ScanAppPaths(items, whitelist, token);
        }
        if (items.Count < MaxResults)
        {
            status?.Report("正在扫描注册表：无效的文件关联");
            ScanFileAssociations(items, whitelist, token);
        }

        return Task.FromResult<IReadOnlyList<ScanItem>>(items.Take(MaxResults).ToArray());
    }

    private static ScanItem? Create(string path, string description, WhitelistMatcher whitelist)
    {
        if (whitelist.IsWhitelisted(path)) return null;
        return new ScanItem
        {
            Category = ScanCategory.RegistryResidue,
            DisplayName = Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar)),
            Path = path,
            SizeBytes = 0,
            Risk = RiskLevel.High,
            IsRegistryEntry = true,
            Description = description,
            IsChecked = false, // 注册表项永远不默认勾选
            RequiresRestart = true
        };
    }

    private static void Add(List<ScanItem> target, string path, string description, WhitelistMatcher whitelist, CancellationToken token)
    {
        if (token.IsCancellationRequested || target.Count >= MaxResults) return;
        var item = Create(path, description, whitelist);
        if (item is not null) target.Add(item);
    }

    private static void ScanUninstallResidues(List<ScanItem> items, WhitelistMatcher whitelist, CancellationToken token)
    {
        var roots = new (string Hive, string SubKey)[]
        {
            ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, subKey) in roots)
        {
            if (token.IsCancellationRequested) break;
            using var parent = OpenKey(hive, subKey, writable: false);
            if (parent is null) continue;
            foreach (var name in parent.GetSubKeyNames())
            {
                if (token.IsCancellationRequested || items.Count >= MaxResults) break;
                try
                {
                    using var key = parent.OpenSubKey(name);
                    if (key is null) continue;
                    if (key.GetValue("SystemComponent") is int systemComponent && systemComponent == 1) continue;

                    var installLocation = key.GetValue("InstallLocation")?.ToString()?.Trim().Trim('"');
                    var displayIcon = key.GetValue("DisplayIcon")?.ToString()?.Trim().Trim('"');
                    var uninstallString = key.GetValue("UninstallString")?.ToString();

                    var reason = string.Empty;
                    if (!string.IsNullOrWhiteSpace(installLocation)
                        && !installLocation.Contains('%')
                        && Path.IsPathRooted(installLocation)
                        && !Directory.Exists(installLocation))
                    {
                        reason = $"安装目录已不存在：{installLocation}";
                    }
                    else if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        var iconPath = displayIcon.Split(',')[0].Trim().Trim('"');
                        if (Path.IsPathRooted(iconPath) && !File.Exists(iconPath)) reason = $"图标文件已不存在：{iconPath}";
                    }
                    else if (string.IsNullOrWhiteSpace(uninstallString)
                             && string.IsNullOrWhiteSpace(key.GetValue("DisplayName")?.ToString()))
                    {
                        reason = "既无显示名称也无卸载命令，疑似残留空项";
                    }

                    if (reason.Length > 0)
                    {
                        var displayName = key.GetValue("DisplayName")?.ToString() ?? name;
                        Add(items, $"{hive}\\{subKey}\\{name}",
                            $"{displayName}｜{reason}（删除前请确认并备份）", whitelist, token);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static void ScanAppPaths(List<ScanItem> items, WhitelistMatcher whitelist, CancellationToken token)
    {
        var roots = new (string Hive, string SubKey)[]
        {
            ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
            ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths")
        };
        foreach (var (hive, subKey) in roots)
        {
            if (token.IsCancellationRequested) break;
            using var parent = OpenKey(hive, subKey, writable: false);
            if (parent is null) continue;
            foreach (var name in parent.GetSubKeyNames())
            {
                if (token.IsCancellationRequested || items.Count >= MaxResults) break;
                try
                {
                    using var key = parent.OpenSubKey(name);
                    var value = key?.GetValue("")?.ToString()?.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var path = value.Split(" /")[0].Trim().Trim('"');
                    if (Path.IsPathRooted(path) && !File.Exists(path))
                    {
                        Add(items, $"{hive}\\{subKey}\\{name}",
                            $"App Paths 指向的程序已不存在：{path}", whitelist, token);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static void ScanFileAssociations(List<ScanItem> items, WhitelistMatcher whitelist, CancellationToken token)
    {
        using var classes = OpenKey("HKCU", @"Software\Classes", writable: false);
        if (classes is null) return;
        foreach (var name in classes.GetSubKeyNames().Where(n => n.StartsWith('.')))
        {
            if (token.IsCancellationRequested || items.Count >= MaxResults) break;
            try
            {
                using var extKey = classes.OpenSubKey(name);
                var progId = extKey?.GetValue("")?.ToString();
                if (string.IsNullOrWhiteSpace(progId)) continue;
                using var progKey = classes.OpenSubKey(progId) ?? Registry.ClassesRoot.OpenSubKey(progId);
                if (progKey is null)
                {
                    Add(items, $"HKCU\\Software\\Classes\\{name}",
                        $"扩展名 {name} 关联的 ProgID「{progId}」已不存在", whitelist, token);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    /// <summary>按 hive 字符串打开注册表键，统一使用 64 位视图（32 位项通过 WOW6432Node 路径访问）。</summary>
    internal static RegistryKey? OpenKey(string hive, string subKey, bool writable)
    {
        try
        {
            var baseKey = hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var key = writable ? baseKey.OpenSubKey(subKey, true) : baseKey.OpenSubKey(subKey);
            if (key is null) baseKey.Dispose();
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
