using SystemAssistant.Models.Cleaning;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.IO;

namespace SystemAssistant.Core.Cleaning;

/// <summary>扫描提供者统一契约。每个提供者只负责「发现」，绝不执行删除。</summary>
internal interface IScanProvider
{
    Task<IReadOnlyList<ScanItem>> ScanAsync(ScanOptions options, WhitelistMatcher whitelist,
        IProgress<string>? status, CancellationToken token);
}

/// <summary>所有扫描提供者的公共构建逻辑。</summary>
internal static class ScanItemBuilder
{
    /// <summary>低风险项默认勾选，中 / 高风险项默认不勾选（保守策略）。</summary>
    private static bool DefaultChecked(RiskLevel risk) => risk == RiskLevel.Low;

    /// <summary>把目录下的直接子项列成可勾选的待清理项。</summary>
    public static IReadOnlyList<ScanItem> FromDirectory(string directory, ScanCategory category,
        RiskLevel risk, string description, WhitelistMatcher whitelist, CancellationToken token,
        int maxItems = 2000)
    {
        var items = new List<ScanItem>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return items;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(maxItems))
            {
                if (token.IsCancellationRequested) break;
                if (whitelist.IsWhitelisted(entry)) continue;
                if (PathGuard.IsSafeToDelete(entry, out _) == false) continue;

                long size;
                bool isDirectory;
                try
                {
                    isDirectory = Directory.Exists(entry);
                    size = isDirectory
                        ? FileSystemHelper.GetDirectorySize(entry, token)
                        : new FileInfo(entry).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                items.Add(new ScanItem
                {
                    Category = category,
                    DisplayName = Path.GetFileName(entry),
                    Path = entry,
                    SizeBytes = size,
                    Risk = risk,
                    IsDirectory = isDirectory,
                    Description = description,
                    IsChecked = DefaultChecked(risk)
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 目录不可读：跳过，不影响其它扫描目标
        }
        return items;
    }

    /// <summary>单个文件（例如 MEMORY.DMP、thumbcache_*.db）。</summary>
    public static IReadOnlyList<ScanItem> FromFiles(IEnumerable<string> files, ScanCategory category,
        RiskLevel risk, string description, WhitelistMatcher whitelist)
    {
        var items = new List<ScanItem>();
        foreach (var file in files)
        {
            if (!File.Exists(file) || whitelist.IsWhitelisted(file)) continue;
            if (!PathGuard.IsSafeToDelete(file, out _)) continue;
            try
            {
                items.Add(new ScanItem
                {
                    Category = category,
                    DisplayName = Path.GetFileName(file),
                    Path = file,
                    SizeBytes = new FileInfo(file).Length,
                    Risk = risk,
                    Description = description,
                    IsChecked = DefaultChecked(risk)
                });
            }
            catch (IOException) { }
        }
        return items;
    }

    public static ScanItem Custom(string path, ScanCategory category, RiskLevel risk, string description,
        long sizeBytes = 0, bool isDirectory = false)
        => new()
        {
            Category = category,
            DisplayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            Path = path,
            SizeBytes = sizeBytes,
            IsDirectory = isDirectory,
            Risk = risk,
            Description = description,
            IsChecked = DefaultChecked(risk)
        };

    public static string LocalAppData(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative);

    public static string AppData(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative);

    public static string Windows(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), relative);

    public static string SizeText(long bytes) => FormatHelper.FormatBytes(bytes);
}

/// <summary>系统垃圾：临时目录、更新缓存、日志、转储、缩略图、预读文件。</summary>
internal sealed class SystemJunkScanProvider : IScanProvider
{
    public Task<IReadOnlyList<ScanItem>> ScanAsync(ScanOptions options, WhitelistMatcher whitelist,
        IProgress<string>? status, CancellationToken token)
    {
        if (!options.ScanSystemJunk) return Task.FromResult<IReadOnlyList<ScanItem>>(Array.Empty<ScanItem>());
        var items = new List<ScanItem>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var targets = new (string Path, ScanCategory Category, RiskLevel Risk, string Description)[]
        {
            (Path.GetTempPath(), ScanCategory.SystemTemp, RiskLevel.Low, "当前用户临时文件夹"),
            (ScanItemBuilder.Windows("Temp"), ScanCategory.SystemTemp, RiskLevel.Medium, "系统临时文件夹"),
            (ScanItemBuilder.Windows(@"SoftwareDistribution\Download"), ScanCategory.WindowsUpdateCache,
                RiskLevel.Medium, "Windows 更新下载缓存（清理后更新历史可能重新下载）"),
            (ScanItemBuilder.Windows(@"Logs\CBS"), ScanCategory.SystemLog, RiskLevel.Medium, "CBS 组件安装日志"),
            (ScanItemBuilder.Windows(@"Logs\DISM"), ScanCategory.SystemLog, RiskLevel.Medium, "DISM 日志"),
            (ScanItemBuilder.LocalAppData(@"Microsoft\Windows\WER"), ScanCategory.SystemLog, RiskLevel.Low, "Windows 错误报告"),
            (ScanItemBuilder.Windows("Minidump"), ScanCategory.ErrorDump, RiskLevel.Medium, "系统小型内存转储"),
            (ScanItemBuilder.LocalAppData("CrashDumps"), ScanCategory.ErrorDump, RiskLevel.Low, "应用程序崩溃转储"),
            (ScanItemBuilder.Windows("Prefetch"), ScanCategory.Prefetch, RiskLevel.High,
                "预读文件（清理后短期内程序启动会变慢，不建议清理）")
        };

        foreach (var target in targets)
        {
            if (token.IsCancellationRequested) break;
            status?.Report("正在扫描：" + target.Path);
            items.AddRange(ScanItemBuilder.FromDirectory(target.Path, target.Category, target.Risk,
                target.Description, whitelist, token));
        }

        // 单独的大文件 / 通配符项
        status?.Report("正在扫描：缩略图缓存与内存转储");
        var explorerCache = ScanItemBuilder.LocalAppData(@"Microsoft\Windows\Explorer");
        if (Directory.Exists(explorerCache))
        {
            try
            {
                items.AddRange(ScanItemBuilder.FromFiles(
                    Directory.EnumerateFiles(explorerCache, "thumbcache_*.db"),
                    ScanCategory.ThumbnailCache, RiskLevel.Low, "资源管理器缩略图缓存", whitelist));
            }
            catch (IOException) { }
        }
        // 通配符必须真正展开：原先用 .Where(p => !p.Contains('*')) 把 *.dmp 整项丢掉，
        // 导致 Minidump 转储清理永远为空。
        var dumpFiles = new List<string> { Path.Combine(windows, "MEMORY.DMP") };
        try
        {
            var minidumpDir = Path.Combine(windows, "Minidump");
            if (Directory.Exists(minidumpDir))
                dumpFiles.AddRange(Directory.EnumerateFiles(minidumpDir, "*.dmp"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        items.AddRange(ScanItemBuilder.FromFiles(dumpFiles,
            ScanCategory.ErrorDump, RiskLevel.Medium, "系统内存转储文件", whitelist));

        return Task.FromResult<IReadOnlyList<ScanItem>>(items);
    }
}

/// <summary>
/// 浏览器缓存：Edge / Chrome / Firefox。
/// 缓存默认勾选；下载记录、历史、Cookie 默认不勾选，且被标记为高风险，必须由用户手动选择。
/// </summary>
internal sealed class BrowserScanProvider : IScanProvider
{
    public Task<IReadOnlyList<ScanItem>> ScanAsync(ScanOptions options, WhitelistMatcher whitelist,
        IProgress<string>? status, CancellationToken token)
    {
        var items = new List<ScanItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var profiles = new (string Name, string UserDataRoot)[]
        {
            ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"))
        };

        foreach (var (browser, root) in profiles)
        {
            if (token.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;
            status?.Report("正在扫描浏览器：" + browser);

            var profileDirs = new List<string> { Path.Combine(root, "Default") };
            try
            {
                profileDirs.AddRange(Directory.EnumerateDirectories(root)
                    .Where(d => Path.GetFileName(d).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }

            foreach (var profile in profileDirs.Where(Directory.Exists))
            {
                var cacheRoots = new[]
                {
                    Path.Combine(profile, "Cache"),
                    Path.Combine(profile, "Code Cache"),
                    Path.Combine(profile, "GPUCache"),
                    Path.Combine(profile, "Service Worker", "CacheStorage")
                };
                if (options.ScanBrowserCache)
                {
                    foreach (var cache in cacheRoots)
                        items.AddRange(ScanItemBuilder.FromDirectory(cache, ScanCategory.BrowserCache,
                            RiskLevel.Low, $"{browser} 浏览器缓存（{Path.GetFileName(profile)}）", whitelist, token));
                }

                // Chromium 的下载记录就存放在 History 这个 SQLite 库里，没有独立文件。
                // 若两个选项各自生成一个同路径条目，CleanService 去重后「下载记录」分类会整体消失，
                // 因此合并为一条，并在描述里明确它同时包含下载记录。
                if (options.ScanBrowserHistory || options.ScanBrowserDownloads)
                {
                    items.AddRange(ScanItemBuilder.FromFiles(new[] { Path.Combine(profile, "History") },
                        ScanCategory.BrowserHistory, RiskLevel.High,
                        $"{browser} 历史记录（含下载记录，同库，删除后无法恢复）", whitelist));
                }
                if (options.ScanBrowserCookies)
                {
                    items.AddRange(ScanItemBuilder.FromFiles(
                        new[] { Path.Combine(profile, "Network", "Cookies"), Path.Combine(profile, "Cookies") },
                        ScanCategory.BrowserCookies, RiskLevel.High,
                        $"{browser} Cookie（删除后网站需要重新登录）", whitelist));
                }
            }
        }

        // Firefox
        var firefoxRoot = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxRoot))
        {
            status?.Report("正在扫描浏览器：Firefox");
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(firefoxRoot))
                {
                    if (options.ScanBrowserCache)
                        items.AddRange(ScanItemBuilder.FromDirectory(Path.Combine(profile, "cache2"),
                            ScanCategory.BrowserCache, RiskLevel.Low, "Firefox 缓存", whitelist, token));
                    if (options.ScanBrowserHistory || options.ScanBrowserDownloads)
                        items.AddRange(ScanItemBuilder.FromFiles(new[] { Path.Combine(profile, "places.sqlite") },
                            ScanCategory.BrowserHistory, RiskLevel.High,
                            "Firefox 历史 / 下载记录（删除后无法恢复）", whitelist));
                    if (options.ScanBrowserCookies)
                        items.AddRange(ScanItemBuilder.FromFiles(new[] { Path.Combine(profile, "cookies.sqlite") },
                            ScanCategory.BrowserCookies, RiskLevel.High,
                            "Firefox Cookie（删除后网站需要重新登录）", whitelist));
                }
            }
            catch (IOException) { }
        }

        return Task.FromResult<IReadOnlyList<ScanItem>>(items);
    }
}

/// <summary>主流软件缓存目录（只针对明确的缓存子目录，不扫描软件安装目录）。</summary>
internal sealed class ApplicationCacheScanProvider : IScanProvider
{
    private static readonly (string Relative, string Name)[] LocalTargets =
    {
        (@"Microsoft\Windows\INetCache", "IE / WebView 缓存"),
        (@"Microsoft\Windows\WebCache", "WebCache 缓存"),
        (@"Microsoft\Windows\Explorer\IconCacheToDelete", "图标缓存待删除项"),
        ("NuGet", "NuGet 全局包缓存"),
        (@"pip\Cache", "pip 下载缓存"),
        (@"npm-cache", "npm 缓存"),
        (@"Yarn\Cache", "Yarn 缓存"),
        (@"Temp", "用户级应用临时目录"),
        (@"Google\Chrome\User Data\Default\Service Worker\ScriptCache", "Chrome 脚本缓存"),
        (@"Microsoft\Edge\User Data\Default\Service Worker\ScriptCache", "Edge 脚本缓存"),
        (@"CrashDumps", "应用崩溃转储")
    };

    public Task<IReadOnlyList<ScanItem>> ScanAsync(ScanOptions options, WhitelistMatcher whitelist,
        IProgress<string>? status, CancellationToken token)
    {
        if (!options.ScanApplicationCache) return Task.FromResult<IReadOnlyList<ScanItem>>(Array.Empty<ScanItem>());
        var items = new List<ScanItem>();
        foreach (var (relative, name) in LocalTargets)
        {
            if (token.IsCancellationRequested) break;
            var path = ScanItemBuilder.LocalAppData(relative);
            status?.Report("正在扫描应用缓存：" + name);
            items.AddRange(ScanItemBuilder.FromDirectory(path, ScanCategory.ApplicationCache,
                RiskLevel.Low, name, whitelist, token, maxItems: 800));
        }
        items.AddRange(ScanItemBuilder.FromDirectory(ScanItemBuilder.AppData(@"npm-cache"),
            ScanCategory.ApplicationCache, RiskLevel.Low, "npm 缓存", whitelist, token, maxItems: 800));
        return Task.FromResult<IReadOnlyList<ScanItem>>(items);
    }
}

/// <summary>用户自定义路径：只把目录的直接子项列出来，不递归展开整个自定义目录本身。</summary>
internal sealed class CustomPathScanProvider : IScanProvider
{
    public Task<IReadOnlyList<ScanItem>> ScanAsync(ScanOptions options, WhitelistMatcher whitelist,
        IProgress<string>? status, CancellationToken token)
    {
        var items = new List<ScanItem>();
        foreach (var path in options.CustomPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (token.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            if (whitelist.IsWhitelisted(path)) continue;
            status?.Report("正在扫描自定义路径：" + path);
            items.AddRange(ScanItemBuilder.FromDirectory(path, ScanCategory.CustomPath, RiskLevel.High,
                "用户自定义扫描路径（请逐项确认）", whitelist, token).Select(i =>
                ScanItemBuilder.Custom(i.Path, ScanCategory.CustomPath, RiskLevel.High, i.Description,
                    i.SizeBytes, i.IsDirectory)));
        }
        return Task.FromResult<IReadOnlyList<ScanItem>>(items);
    }
}
