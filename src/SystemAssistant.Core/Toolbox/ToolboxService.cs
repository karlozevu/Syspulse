using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Toolbox;
using SystemAssistant.Utils.IO;
using SystemAssistant.Utils.Security;

namespace SystemAssistant.Core.Toolbox;

/// <summary>独立工具箱：文件粉碎、大文件查找、重复文件查找。</summary>
public sealed class ToolboxService : IToolboxService
{
    private readonly IOperationLogService _log;

    public ToolboxService(IOperationLogService log) => _log = log;

    public async Task<int> ShredAsync(IReadOnlyList<string> files, ShredOptions options,
        IProgress<ShredProgress>? progress = null, CancellationToken token = default)
    {
        var success = 0;
        var index = 0;
        foreach (var file in files)
        {
            if (token.IsCancellationRequested) break;
            index++;
            progress?.Report(new ShredProgress
            {
                CurrentFile = Path.GetFileName(file),
                Total = files.Count,
                Processed = index,
                CurrentPass = options.Passes
            });
            var error = await FileShredder.ShredFileAsync(file, options, token);
            _log.Log(OperationType.File, "粉碎文件", file, error.Length == 0, error.Length == 0 ? null : error);
            if (error.Length == 0) success++;
        }
        return success;
    }

    public Task<IReadOnlyList<FileEntry>> FindLargeFilesAsync(LargeFileScanOptions options,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            status?.Report("正在扫描大文件：" + options.RootPath);
            var result = new List<FileEntry>();
            foreach (var file in FileSystemHelper.EnumerateFilesSafe(options.RootPath, true, token))
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (file.Length < options.MinimumSizeBytes) continue;
                    result.Add(ToEntry(file.FullName, file.Length, file.LastWriteTime));
                }
                catch (IOException) { }
            }
            status?.Report("扫描完成，正在排序");
            return (IReadOnlyList<FileEntry>)result
                .OrderByDescending(f => f.SizeBytes)
                .Take(options.MaxResults)
                .ToArray();
        }, token);
    }

    public Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(DuplicateScanOptions options,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        // 整盘枚举是同步操作，若放在首个 await 之前会直接阻塞调用方（UI 线程）。
        // 把整个方法体放进 Task.Run，保证从第一行起就运行在后台线程。
        return Task.Run<IReadOnlyList<DuplicateGroup>>(async () =>
        {
            status?.Report("正在枚举文件并按大小分组");
            var bySize = new Dictionary<long, List<string>>();
            foreach (var file in FileSystemHelper.EnumerateFilesSafe(options.RootPath, true, token))
            {
                token.ThrowIfCancellationRequested();
                if (!TryGetFileStats(file.FullName, out var size, out _)) continue;
                if (size < options.MinimumSizeBytes) continue;
                if (!bySize.TryGetValue(size, out var list))
                {
                    list = new List<string>();
                    bySize[size] = list;
                }
                list.Add(file.FullName);
            }

            var groups = new List<DuplicateGroup>();
            var candidates = bySize.Values.Where(list => list.Count > 1).ToList();
            var processed = 0;
            foreach (var sizeGroup in candidates)
            {
                token.ThrowIfCancellationRequested();
                processed++;
                if (processed % 20 == 0) status?.Report($"正在比对内容：{processed}/{candidates.Count} 组");

                // 第一层：快速指纹（长度 + 头尾 8 KB）
                var byFingerprint = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in sizeGroup)
                {
                    try
                    {
                        var fingerprint = await HashHelper.ComputeQuickFingerprintAsync(path, token);
                        if (!byFingerprint.TryGetValue(fingerprint, out var list))
                        {
                            list = new List<string>();
                            byFingerprint[fingerprint] = list;
                        }
                        list.Add(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    // OperationCanceledException 不在此吞掉，交由 Task 正常取消
                }

                // 第二层：只有候选组才做完整 SHA256 比对，确保「内容完全一致」
                foreach (var quickGroup in byFingerprint.Values.Where(list => list.Count > 1))
                {
                    var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in quickGroup)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var hash = await HashHelper.ComputeSha256Async(path, token);
                            if (!byHash.TryGetValue(hash, out var list))
                            {
                                list = new List<string>();
                                byHash[hash] = list;
                            }
                            list.Add(path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        // 取消异常继续向上传播
                    }

                    foreach (var (hash, paths) in byHash.Where(pair => pair.Value.Count > 1))
                    {
                        var entries = new List<FileEntry>();
                        foreach (var path in paths)
                        {
                            if (!TryGetFileStats(path, out var entrySize, out var lastWrite)) continue;
                            entries.Add(ToEntry(path, entrySize, lastWrite, hash));
                        }
                        if (entries.Count == 0) continue;

                        groups.Add(new DuplicateGroup
                        {
                            Hash = hash,
                            SizeBytes = entries[0].SizeBytes,
                            Files = entries.ToArray()
                        });
                        if (groups.Count >= options.MaxResults) return groups;
                    }
                }
            }
            status?.Report($"扫描完成，发现 {groups.Count} 组重复文件");
            return groups;
        }, token);
    }

    /// <summary>安全读取 FileInfo，失效 / 被删除 / 无权限的文件直接跳过。</summary>
    private static bool TryGetFileStats(string path, out long size, out DateTime lastWrite)
    {
        size = 0;
        lastWrite = DateTime.MinValue;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWrite = info.LastWriteTime;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static FileEntry ToEntry(string path, long size, DateTime lastWrite, string hash = "") => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        DirectoryName = Path.GetDirectoryName(path) ?? string.Empty,
        SizeBytes = size,
        LastWriteTime = lastWrite,
        Hash = hash,
        IsChecked = false // 用户主动勾选后才允许删除
    };
}
