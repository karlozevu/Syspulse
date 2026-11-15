using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Cleaning;
using SystemAssistant.Models.Common;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.IO;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.Core.Cleaning;

/// <summary>
/// 安全清理编排器：扫描（只读）→ 用户勾选 → 二次确认 → 删除（带进度与日志）。
/// 程序不会自动决定删除任何东西；只有 IsChecked 为 true 的项才会进入删除阶段。
/// </summary>
public sealed class CleanService : ICleanService
{
    private readonly ISettingsService _settings;
    private readonly IOperationLogService _log;
    private readonly IReadOnlyList<IScanProvider> _providers;
    private volatile bool _registryBackupDone;

    public CleanService(ISettingsService settings, IOperationLogService log)
    {
        _settings = settings;
        _log = log;
        _providers = new IScanProvider[]
        {
            new SystemJunkScanProvider(),
            new BrowserScanProvider(),
            new ApplicationCacheScanProvider(),
            new RegistryScanProvider(),
            new CustomPathScanProvider()
        };
    }

    /// <summary>本次会话是否已完成注册表备份（未备份时拒绝执行任何注册表删除）。</summary>
    public bool RegistryBackupDone => _registryBackupDone;

    public async Task<ScanResult> ScanAsync(ScanOptions options, IProgress<string>? status = null,
        CancellationToken token = default)
    {
        var whitelist = new WhitelistMatcher(_settings.Current.Whitelist);
        whitelist.ResetCounter();
        var items = new List<ScanItem>();
        var warnings = new List<string>();

        foreach (var provider in _providers)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                var found = await provider.ScanAsync(options, whitelist, status, token);
                items.AddRange(found);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{provider.GetType().Name}：{ex.Message}");
            }
        }

        // 去重：同一路径可能被多个提供者命中（例如用户临时目录同时是应用缓存目录）
        var distinct = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => (int)i.Risk).First())
            .OrderBy(i => (int)i.Category).ThenByDescending(i => i.SizeBytes)
            .ToArray();

        var groups = distinct.GroupBy(i => i.Category)
            .Select(g => new ScanGroup
            {
                Category = g.Key,
                Title = CategoryTitle(g.Key),
                Description = CategoryDescription(g.Key),
                Items = g.ToArray(),
                TotalSizeBytes = g.Sum(i => i.SizeBytes)
            })
            .OrderBy(g => (int)g.Category)
            .ToArray();

        if (options.ScanBrowserHistory || options.ScanBrowserCookies || options.ScanBrowserDownloads)
            warnings.Add("历史记录 / Cookie / 下载记录属于高风险项，默认不勾选，删除后无法恢复。");
        if (options.ScanRegistryResidue)
            warnings.Add("注册表项仅做保守扫描；执行删除前必须完成注册表备份并逐项确认。");
        if (IsAnyBrowserRunning())
            warnings.Add("检测到浏览器正在运行，相关缓存文件可能被占用而删除失败，建议先关闭浏览器。");

        status?.Report($"扫描完成：共 {distinct.Length} 项");
        return new ScanResult
        {
            ScannedAt = DateTime.Now,
            Groups = groups,
            TotalItemCount = distinct.Length,
            TotalSizeBytes = distinct.Sum(i => i.SizeBytes),
            SkippedByWhitelistCount = whitelist.SkippedCount,
            Warnings = warnings
        };
    }

    /// <summary>
    /// 执行删除。highRiskConfirmed 必须由 UI 的二次确认弹窗置为 true，
    /// 否则所有中 / 高风险项都会被跳过，只执行低风险项。
    /// </summary>
    public async Task<CleanResult> CleanAsync(IReadOnlyList<ScanItem> confirmedItems, bool highRiskConfirmed,
        IProgress<CleanProgress>? progress = null, CancellationToken token = default)
    {
        var whitelist = new WhitelistMatcher(_settings.Current.Whitelist);
        var candidates = confirmedItems.Where(i => i.IsChecked).DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var errors = new List<string>();
        var succeeded = 0;
        var failed = 0;
        long reclaimed = 0;

        if (candidates.Any(i => i.IsRegistryEntry) && !_registryBackupDone)
        {
            var message = "未检测到本次会话的注册表备份，已拒绝执行注册表删除";
            errors.Add(message);
            _log.Log(OperationType.Security, "拒绝注册表清理", message, false);
            candidates = candidates.Where(i => !i.IsRegistryEntry).ToArray();
            failed++;
        }

        var index = 0;
        foreach (var item in candidates)
        {
            if (token.IsCancellationRequested) break;
            index++;
            progress?.Report(new CleanProgress
            {
                Total = candidates.Length,
                Processed = index,
                CurrentItem = item.DisplayName
            });

            if (item.Risk > RiskLevel.Low && !highRiskConfirmed)
            {
                errors.Add($"跳过（未二次确认）：{item.Path}");
                _log.Log(OperationType.Clean, "跳过中高风险项", item.Path, false, "用户未完成二次确认");
                failed++;
                continue;
            }

            if (whitelist.IsWhitelisted(item.Path, count: false))
            {
                errors.Add($"跳过（白名单）：{item.Path}");
                failed++;
                continue;
            }

            var (success, error) = item.IsRegistryEntry
                ? DeleteRegistryItem(item.Path)
                : FileSystemHelper.DeletePermanently(item.Path);

            if (success)
            {
                succeeded++;
                reclaimed += item.SizeBytes;
                _log.Log(item.IsRegistryEntry ? OperationType.Registry : OperationType.Clean,
                    item.IsRegistryEntry ? "删除注册表项" : "删除清理项", item.Path, true);
            }
            else
            {
                failed++;
                errors.Add($"{item.Path}：{error}");
                _log.Log(item.IsRegistryEntry ? OperationType.Registry : OperationType.Clean,
                    item.IsRegistryEntry ? "删除注册表项" : "删除清理项", item.Path, false, error);
            }

            // 让出线程，避免大量小文件删除时进度界面卡顿
            if (index % 20 == 0) await Task.Yield();
        }

        var summary = $"清理完成：成功 {succeeded} 项，失败 / 跳过 {failed} 项，释放约 {FormatHelper.FormatBytes(reclaimed)}";
        _log.Log(OperationType.Clean, "清理汇总", summary, failed == 0);
        return new CleanResult
        {
            SucceededCount = succeeded,
            FailedCount = failed,
            ReclaimedBytes = reclaimed,
            Errors = errors
        };
    }

    /// <summary>把注册表导出到用户选择目录。成功后才允许执行注册表删除。</summary>
    public async Task<RegistryBackupResultHolder> BackupRegistryAsync(string directory, CancellationToken token = default)
    {
        var result = await RegistryBackupHelper.BackupAsync(RegistryBackupHelper.DefaultKeys, directory, token);
        _registryBackupDone = result.Success;
        _log.Log(OperationType.Registry, "注册表备份",
            $"目录 {result.Directory}，成功导出 {result.Files.Count} 个键", result.Success,
            result.Success ? null : string.Join("；", result.Errors));
        return new RegistryBackupResultHolder
        {
            Success = result.Success,
            Directory = result.Directory,
            ManifestPath = result.ManifestPath,
            Errors = result.Errors
        };
    }

    /// <summary>删除单个注册表子键。路径形如 HKLM\SOFTWARE\...\KeyName。</summary>
    private static (bool Success, string Error) DeleteRegistryItem(string registryPath)
    {
        var parts = registryPath.Split('\\');
        if (parts.Length < 3) return (false, "注册表路径格式非法");
        var hive = parts[0];
        if (!hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            && !hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
            return (false, "不支持的注册表根键");

        var keyName = parts[^1];
        var parentPath = string.Join('\\', parts.Skip(1).Take(parts.Length - 2));
        try
        {
            using var parent = RegistryScanProvider.OpenKey(hive, parentPath, writable: true);
            if (parent is null) return (false, "无法打开父键（可能需要管理员权限）");
            parent.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Security.SecurityException)
        {
            return (false, ex.Message);
        }
    }

    private static bool IsAnyBrowserRunning()
    {
        try
        {
            var names = new[] { "msedge", "chrome", "firefox" };
            return System.Diagnostics.Process.GetProcesses().Any(p => names.Contains(
                p.ProcessName, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string CategoryTitle(ScanCategory category) => category switch
    {
        ScanCategory.SystemTemp => "临时文件夹",
        ScanCategory.WindowsUpdateCache => "Windows 更新缓存",
        ScanCategory.SystemLog => "系统日志 / 错误报告",
        ScanCategory.ErrorDump => "错误转储文件",
        ScanCategory.ThumbnailCache => "缩略图缓存",
        ScanCategory.Prefetch => "预读文件",
        ScanCategory.BrowserCache => "浏览器缓存",
        ScanCategory.BrowserHistory => "浏览器历史记录",
        ScanCategory.BrowserCookies => "浏览器 Cookie",
        ScanCategory.BrowserDownloads => "浏览器下载记录",
        ScanCategory.ApplicationCache => "应用缓存",
        ScanCategory.RegistryResidue => "注册表残留",
        ScanCategory.CustomPath => "自定义路径",
        _ => "其它"
    };

    private static string CategoryDescription(ScanCategory category) => category switch
    {
        ScanCategory.BrowserHistory or ScanCategory.BrowserCookies or ScanCategory.BrowserDownloads
            or ScanCategory.RegistryResidue or ScanCategory.CustomPath or ScanCategory.Prefetch
            => "高风险：默认不勾选，删除前需二次确认，且删除后不可恢复",
        ScanCategory.WindowsUpdateCache or ScanCategory.SystemLog or ScanCategory.ErrorDump
            => "中风险：默认不勾选，请确认不再需要这些日志 / 缓存",
        _ => "低风险：通常可以安全清理，仍请核对列表"
    };
}
