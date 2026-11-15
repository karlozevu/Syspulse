namespace SystemAssistant.Utils.IO;

/// <summary>
/// 删除操作的路径安全护栏。任何清理、粉碎、大文件 / 重复文件删除都必须先经过这里。
/// 这是防止误删系统目录的最后一道技术防线（界面上的二次确认是另一道）。
/// </summary>
public static class PathGuard
{
    private static readonly HashSet<string> CriticalFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bootmgr", "bootmgr.efi", "ntldr", "ntdetect.com", "pagefile.sys",
        "hiberfil.sys", "swapfile.sys", "ntuser.dat"
    };

    /// <summary>
    /// 检查路径是否允许被删除。返回 false 时调用方必须跳过该项并提示用户。
    /// </summary>
    public static bool IsSafeToDelete(string path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "路径为空";
            return false;
        }

        // 注册表项由专门的注册表清理逻辑处理，不走文件删除接口
        if (path.StartsWith("HK", StringComparison.OrdinalIgnoreCase) && path.Contains('\\'))
        {
            reason = "注册表项不能使用文件删除接口";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "路径格式非法";
            return false;
        }

        var normalized = Normalize(full);

        // 禁止盘符根目录
        if (Normalize(Path.GetPathRoot(full) ?? string.Empty).Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            reason = "禁止删除磁盘根目录";
            return false;
        }

        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            reason = "路径指向目录本身";
            return false;
        }
        if (CriticalFileNames.Contains(name))
        {
            reason = "系统关键文件禁止删除";
            return false;
        }

        // ① 明确允许清理的系统临时子目录（只允许其子项，不允许删除该目录本身）
        foreach (var allowed in AllowedSystemSubPaths())
        {
            if (IsStrictChild(normalized, allowed)) return true;
        }

        // ② 其余系统目录一律禁止
        foreach (var root in ProtectedRoots())
        {
            if (IsSameOrChild(normalized, root))
            {
                reason = $"路径位于受保护的系统目录内：{root}";
                return false;
            }
        }

        // ③ 用户个人目录根目录及其一级子目录禁止整体删除
        var userProfile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (IsSame(normalized, userProfile) || IsDirectChildOf(normalized, userProfile))
        {
            reason = "禁止删除用户目录或其一级子目录";
            return false;
        }
        foreach (var special in PersonalRoots())
        {
            if (IsSame(normalized, special))
            {
                reason = $"禁止删除个人目录：{special}";
                return false;
            }
        }

        return true;
    }

    /// <summary>相对路径 / 路径穿越检测。</summary>
    public static bool ContainsTraversal(string path) =>
        path.Contains("..\\", StringComparison.Ordinal) || path.Contains("../", StringComparison.Ordinal);

    /// <summary>系统目录中允许清理的白名单子目录（内容由扫描器枚举，不删除目录本身）。</summary>
    private static IEnumerable<string> AllowedSystemSubPaths()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) yield break;
        yield return Path.Combine(windows, "Temp");
        yield return Path.Combine(windows, "SoftwareDistribution", "Download");
        yield return Path.Combine(windows, "Prefetch");
        yield return Path.Combine(windows, "Minidump");
        yield return Path.Combine(windows, "Logs", "CBS");
        yield return Path.Combine(windows, "Logs", "MeasuredBoot");
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS")
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) yield return Normalize(candidate);
        }
    }

    private static IEnumerable<string> PersonalRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) yield return Normalize(candidate);
        }
    }

    private static bool IsSame(string a, string b) => Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChild(string path, string root) =>
        IsSame(path, root) || IsStrictChild(path, root);

    private static bool IsStrictChild(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        return r.Length > 0 && p.Length > r.Length
               && p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectChildOf(string path, string root)
    {
        if (!IsStrictChild(path, root)) return false;
        var r = Normalize(root);
        return !Normalize(path)[(r.Length + 1)..].Contains('\\');
    }

    private static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
