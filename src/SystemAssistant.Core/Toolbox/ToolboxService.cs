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

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(DuplicateScanOptions options,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        status?.Report("正在枚举文件并按大小分组");
        var bySize = new Dictionary<long, List<string>>();
        foreach (var file in FileSystemHelper.EnumerateFilesSafe(options.RootPath, true, token))
        {
            if (token.IsCancellationRequested) break;
            if (file.Length < options.MinimumSizeBytes) continue;
            if (!bySize.TryGetValue(file.Length, out var list))
            {
                list = new List<string>();
                bySize[file.Length] = list;
            }
            list.Add(file.FullName);
        }

        var groups = new List<DuplicateGroup>();
        var candidates = bySize.Values.Where(list => list.Count > 1).ToList();
        var processed = 0;
        foreach (var sizeGroup in candidates)
        {
            if (token.IsCancellationRequested) break;
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            }

            // 第二层：只有候选组才做完整 SHA256 比对，确保「内容完全一致」
            foreach (var quickGroup in byFingerprint.Values.Where(list => list.Count > 1))
            {
                var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in quickGroup)
                {
                    if (token.IsCancellationRequested) break;
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
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
                }

                foreach (var (hash, paths) in byHash.Where(pair => pair.Value.Count > 1))
                {
                    groups.Add(new DuplicateGroup
                    {
                        Hash = hash,
                        SizeBytes = sizeGroup.Count > 0 ? new FileInfo(paths[0]).Length : 0,
                        Files = paths.Select(p =>
                        {
                            var info = new FileInfo(p);
                            return ToEntry(p, info.Length, info.LastWriteTime, hash);
                        }).ToArray()
                    });
                    if (groups.Count >= options.MaxResults) return groups;
                }
            }
        }
        status?.Report($"扫描完成，发现 {groups.Count} 组重复文件");
        return groups;
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
