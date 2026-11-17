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
        if (PathGuard.HasReparsePointInPath(path))
            return "路径中包含符号链接 / 重解析点，拒绝粉碎";
        if (!File.Exists(path)) return "文件不存在";

        var passes = Math.Clamp(options.Passes, 1, 7);
        var currentPath = path;
        var overwritten = false;
        var cancelled = false;
        try
        {
            File.SetAttributes(currentPath, FileAttributes.Normal);
            using (var stream = new FileStream(currentPath, FileMode.Open, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.WriteThrough))
            {
                var length = stream.Length;
                for (var pass = 0; pass < passes && !cancelled; pass++)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
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
                        // 一旦写入过数据，取消后文件就是「部分覆写」的损坏状态，必须删除
                        overwritten = true;
                        await stream.WriteAsync(buffer.AsMemory(0, chunk), token);
                        remaining -= chunk;
                    }
                    await stream.FlushAsync(token);
                    stream.Flush(flushToDisk: true);
                }
            } // 流必须先释放，否则 File.Delete 会因独占句柄失败

            if (cancelled) return CleanupAfterCancel(currentPath, overwritten);

            if (options.RenameBeforeDelete)
            {
                var directory = Path.GetDirectoryName(currentPath);
                // GetDirectoryName 可能返回 null（相对路径 / 根目录），此时跳过重命名直接删除
                if (!string.IsNullOrEmpty(directory))
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var newName = Path.Combine(directory, RandomNumberGenerator.GetHexString(24));
                        File.Move(currentPath, newName);
                        currentPath = newName;
                    }
                }
            }

            File.Delete(currentPath);
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 覆写期间的取消（WriteAsync / FlushAsync 抛出）同样要完成清理
            return CleanupAfterCancel(currentPath, overwritten);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 覆写中途磁盘满 / 设备故障时，文件处于"前段已覆写、后段仍为原文"的
            // 半销毁状态，比不粉碎更危险（用户可能误以为失败而放弃）。与取消路径
            // 一致：只要写入过就删除并明确告知。
            if (overwritten)
                return CleanupAfterCancel(currentPath, overwritten) + $"（写入错误：{ex.Message}）";
            return ex.Message;
        }
    }

    /// <summary>
    /// 取消时清理现场：只要已经覆写过，就必须把损坏的文件删除，
    /// 并在结果里明确告知用户文件已被销毁，避免留下「半覆写」的幽灵文件。
    /// </summary>
    private static string CleanupAfterCancel(string path, bool overwritten)
    {
        if (!overwritten) return "用户取消了操作";
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return "用户取消了操作；文件已被覆写并销毁，内容不可恢复，请勿继续使用";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"用户取消了操作；文件已被覆写但删除失败（{ex.Message}），文件已损坏，请手动处理";
        }
    }
}
