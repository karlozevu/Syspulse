namespace SystemAssistant.Models.Toolbox;

/// <summary>工具箱通用的文件结果项。</summary>
public sealed class FileEntry
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DirectoryName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }
    public string Hash { get; init; } = string.Empty;
    public bool IsChecked { get; set; }
}

/// <summary>内容完全一致的一组重复文件。</summary>
public sealed class DuplicateGroup
{
    public string Hash { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public IReadOnlyList<FileEntry> Files { get; init; } = Array.Empty<FileEntry>();
    public long WastedBytes => Math.Max(0, SizeBytes * Math.Max(0, Files.Count - 1));
}

/// <summary>文件粉碎选项。</summary>
public sealed class ShredOptions
{
    /// <summary>覆写次数（1~7，默认 3）。</summary>
    public int Passes { get; init; } = 3;

    /// <summary>删除前随机重命名，进一步降低恢复可能性。</summary>
    public bool RenameBeforeDelete { get; init; } = true;
}

/// <summary>粉碎进度。</summary>
public sealed class ShredProgress
{
    public string CurrentFile { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Processed { get; init; }
    public int CurrentPass { get; init; }
    public double Percent => Total <= 0 ? 0 : Processed * 100.0 / Total;
}

/// <summary>大文件扫描选项。</summary>
public sealed class LargeFileScanOptions
{
    public string RootPath { get; init; } = string.Empty;

    /// <summary>只返回大于该阈值的文件（字节）。</summary>
    public long MinimumSizeBytes { get; init; } = 100L * 1024 * 1024;

    public int MaxResults { get; init; } = 500;
}

/// <summary>重复文件扫描选项。</summary>
public sealed class DuplicateScanOptions
{
    public string RootPath { get; init; } = string.Empty;
    public long MinimumSizeBytes { get; init; } = 1024 * 1024;
    public int MaxResults { get; init; } = 500;
}
