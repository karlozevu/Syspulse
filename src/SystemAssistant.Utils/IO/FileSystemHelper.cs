using System.Diagnostics;

namespace SystemAssistant.Utils.IO;

/// <summary>安全文件操作：容错枚举、体积统计、带护栏的删除、资源管理器定位。</summary>
public static class FileSystemHelper
{
    /// <summary>容错枚举文件：跳过无权限目录、符号链接（避免循环）与临时错误。</summary>
    public static IEnumerable<FileInfo> EnumerateFilesSafe(string root, bool recursive = true, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        using var disposable = enumerator;
        while (true)
        {
            if (token.IsCancellationRequested) yield break;
            string current;
            try
            {
                if (!enumerator.MoveNext()) break;
                current = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            FileInfo? info = null;
            try
            {
                info = new FileInfo(current);
                _ = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                info = null;
            }
            if (info is not null) yield return info;
        }
    }

    /// <summary>统计目录体积（字节），遇到无权限文件自动跳过。</summary>
    public static long GetDirectorySize(string root, CancellationToken token = default)
    {
        long total = 0;
        foreach (var file in EnumerateFilesSafe(root, true, token))
        {
            try { total += file.Length; }
            catch (IOException) { /* 文件可能在枚举期间被删除 */ }
        }
        return total;
    }

    /// <summary>
    /// 永久删除文件或目录。删除前强制经过 <see cref="PathGuard"/> 校验，
    /// 并拒绝路径中带有符号链接 / junction 的项，避免删除越界到真实目标。
    /// </summary>
    public static (bool Success, string Error) DeletePermanently(string path)
    {
        if (!PathGuard.IsSafeToDelete(path, out var reason)) return (false, reason);
        if (PathGuard.HasReparsePointInPath(path))
            return (false, "路径中包含符号链接 / 重解析点，拒绝永久删除");

        try
        {
            if (Directory.Exists(path))
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            else
            {
                return (true, string.Empty); // 已被其它程序清理，视为成功
            }
            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 把文件送入回收站（用于「大文件 / 重复文件」这类用户文件删除）。
    /// 回收站不可用时绝不自动降级为永久删除：失败即返回失败，由调用方提示用户。
    /// </summary>
    public static (bool Success, string Error, bool UsedRecycleBin) DeleteToRecycleBin(string path)
    {
        if (!PathGuard.IsSafeToDelete(path, out var reason)) return (false, reason, false);
        if (PathGuard.HasReparsePointInPath(path))
            return (false, "路径中包含符号链接 / 重解析点，拒绝删除", false);
        try
        {
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else if (File.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            return (true, string.Empty, true);
        }
        catch (Exception ex)
        {
            // 安全底线：回收站删除失败时绝不静默降级为永久删除，
            // 必须把失败明确返回给调用方，由用户决定后续处理方式。
            return (false, "送入回收站失败：" + ex.Message, false);
        }
    }

    /// <summary>在资源管理器中定位文件 / 打开所在目录。</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var argument = File.Exists(full) ? $"/select,\"{full}\""
                : Directory.Exists(full) ? $"\"{full}\""
                : $"\"{Path.GetDirectoryName(full) ?? full}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 打开资源管理器失败不影响主流程
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in EnumerateFilesSafe(directory))
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file.FullName, file.Attributes & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
