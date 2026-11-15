namespace SystemAssistant.Models.Cleaning;

/// <summary>扫描分组。</summary>
public enum ScanCategory
{
    SystemTemp,
    WindowsUpdateCache,
    SystemLog,
    ErrorDump,
    ThumbnailCache,
    Prefetch,
    BrowserCache,
    BrowserHistory,
    BrowserCookies,
    BrowserDownloads,
    ApplicationCache,
    RegistryResidue,
    CustomPath
}

/// <summary>风险等级。High 项默认不勾选且必须二次确认。</summary>
public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>待清理项。IsChecked 由用户勾选，永远不会由程序自动置为 true 的高危项。</summary>
public sealed class ScanItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ScanCategory Category { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>文件或目录的完整路径；注册表项使用形如 HKLM\Software\... 的表示。</summary>
    public string Path { get; init; } = string.Empty;

    public long SizeBytes { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public bool IsDirectory { get; init; }
    public bool IsRegistryEntry { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>是否为用户确认要清理的项。低风险项默认勾选，高危项默认不勾选。</summary>
    public bool IsChecked { get; set; }

    /// <summary>清理后是否需要重启才能完全生效。</summary>
    public bool RequiresRestart { get; init; }
}

/// <summary>按类别分组展示。</summary>
public sealed class ScanGroup
{
    public ScanCategory Category { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<ScanItem> Items { get; init; } = Array.Empty<ScanItem>();
    public long TotalSizeBytes { get; init; }
}

/// <summary>一次扫描的结果。</summary>
public sealed class ScanResult
{
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<ScanGroup> Groups { get; init; } = Array.Empty<ScanGroup>();
    public long TotalSizeBytes { get; init; }
    public int TotalItemCount { get; init; }
    public int SkippedByWhitelistCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>白名单规则：目录 / 后缀 / 文件名，命中即跳过。</summary>
public sealed class WhitelistRule
{
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Directory / Extension / FileName</summary>
    public string Kind { get; init; } = "Directory";

    /// <summary>是否启用。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>扫描选项。</summary>
public sealed class ScanOptions
{
    public bool ScanSystemJunk { get; init; } = true;
    public bool ScanBrowserCache { get; init; } = true;
    public bool ScanBrowserHistory { get; init; }
    public bool ScanBrowserCookies { get; init; }
    public bool ScanBrowserDownloads { get; init; }
    public bool ScanApplicationCache { get; init; } = true;
    public bool ScanRegistryResidue { get; init; }

    /// <summary>用户自定义扫描目录。</summary>
    public IReadOnlyList<string> CustomPaths { get; init; } = Array.Empty<string>();

    /// <summary>最大扫描文件数，防止超大盘扫描卡死。</summary>
    public int MaxFileCount { get; init; } = 200_000;
}

/// <summary>删除进度。</summary>
public sealed class CleanProgress
{
    public int Total { get; init; }
    public int Processed { get; init; }
    public string CurrentItem { get; init; } = string.Empty;
    public double Percent => Total <= 0 ? 0 : Processed * 100.0 / Total;
}

/// <summary>清理结果。</summary>
public sealed class CleanResult
{
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public long ReclaimedBytes { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
