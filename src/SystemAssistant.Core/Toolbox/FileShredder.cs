using System.Security.Cryptography;
using SystemAssistant.Models.Toolbox;
using SystemAssistant.Utils.IO;

namespace SystemAssistant.Core.Toolbox;

/// <summary>
/// 文件粉碎：多次覆写文件内容后删除，降低被数据恢复软件还原的可能性。
/// 注意：SSD / 带磨损均衡与 TRIM 的存储、以及云同步目录无法保证物理层不可恢复，
/// 界面与 README 都会明确标注这一限制。
/// </summary>
public static class FileShredder
{
    private const int BufferSize = 64 * 1024;

    /// <summary>粉碎单个文件，返回错误信息（成功时为空字符串）。</summary>
    public static async Task<string> ShredFileAsync(string path, ShredOptions options, CancellationToken token = default)
    {
        if (!PathGuard.IsSafeToDelete(path, out var reason)) return reason;
        if (!File.Exists(path)) return "文件不存在";

        var passes = Math.Clamp(options.Passes, 1, 7);
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.WriteThrough))
            {
                var length = stream.Length;
                for (var pass = 0; pass < passes; pass++)
                {
                    if (token.IsCancellationRequested) return "用户取消了操作";
                    stream.Seek(0, SeekOrigin.Begin);
                    var buffer = new byte[BufferSize];
                    long remaining = length;
                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(buffer.Length, remaining);
                        // 第 1 轮随机数据，第 2 轮全 0，第 3 轮全 1，之后交替
                        switch (pass % 3)
                        {
                            case 0: RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk)); break;
                            case 1: Array.Clear(buffer, 0, chunk); break;
                            default: buffer.AsSpan(0, chunk).Fill(0xFF); break;
                        }
                        await stream.WriteAsync(buffer.AsMemory(0, chunk), token);
                        remaining -= chunk;
                    }
                    await stream.FlushAsync(token);
                    stream.Flush(flushToDisk: true);
                }
            }

            if (options.RenameBeforeDelete)
            {
                // 多次随机重命名，去掉原文件名与扩展名的关联
                for (var i = 0; i < 3; i++)
                {
                    var directory = Path.GetDirectoryName(path)!;
                    var newName = Path.Combine(directory, RandomNumberGenerator.GetHexString(24));
                    File.Move(path, newName);
                    path = newName;
                }
            }

            File.Delete(path);
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return "用户取消了操作";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }
    }
}
