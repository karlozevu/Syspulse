using System.Security.Cryptography;

namespace SystemAssistant.Utils.Security;

/// <summary>文件哈希计算，用于重复文件比对。</summary>
public static class HashHelper
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>计算整个文件的 SHA256（小写十六进制）。</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 计算「快速指纹」：文件长度 + 头部 8 KB + 尾部 8 KB。
    /// 重复文件查找先用它做粗筛，只有候选组才计算完整 SHA256，避免全盘读取。
    /// </summary>
    public static async Task<string> ComputeQuickFingerprintAsync(string path, CancellationToken token = default)
    {
        const int chunkSize = 8 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var buffer = new byte[chunkSize];

        var headRead = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), token);
        string tailPart;
        if (length > chunkSize * 2)
        {
            stream.Seek(-chunkSize, SeekOrigin.End);
            var tailRead = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), token);
            tailPart = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, tailRead)));
        }
        else
        {
            tailPart = "-";
        }

        var headPart = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, headRead)));
        return $"{length}:{headPart}:{tailPart}";
    }
}
