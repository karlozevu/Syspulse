using System.Runtime.InteropServices;
using System.Text;

namespace SystemAssistant.Utils.IO;

/// <summary>
/// 删除操作的路径安全护栏。任何清理、粉碎、大文件 / 重复文件删除都必须先经过这里。
/// 这是防止误删系统目录的最后一道技术防线（界面上的二次确认是另一道）。
/// </summary>
public static class PathGuard
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferSize);

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

        var trimmed = path.Trim().Trim('"');

        // 注册表项由专门的注册表清理逻辑处理，不走文件删除接口；前缀必须精确匹配，
        // 避免把普通文件误判，也避免 HKLM/HKCU/HKEY_ 之外的伪前缀绕过。
        if (trimmed.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
        {
            reason = "注册表项不能使用文件删除接口";
            return false;
        }

        // 显式拒绝设备路径与 UNC 路径：\\?\、\\.\、\\server\share 都可能绕过
        // 后面的字符串比较（GetFullPath 对它们的处理与本地路径不同）。
        var slashNormalized = trimmed.Replace('/', '\\');
        if (slashNormalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "禁止使用 \\\\?\\ / \\\\.\\ 设备路径或 UNC 路径删除";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
            // .NET Core 3.0+ 的 GetFullPath 保留路径段末尾的点 / 空格，而 Win32 文件系统
            // 会把 "C:\Windows." 解析为 "C:\Windows"。若校验用带点的字符串、删除用原字符串，
            // 就可能绕过受保护目录判定误删系统目录。这里显式拒绝任何带尾随点 / 空格的段。
            if (HasTrailingDotOrSpace(trimmed))
            {
                reason = "路径段含尾随点或空格，可能被 Windows 解析为其它路径，拒绝删除";
                return false;
            }
            // 展开 8.3 短名后再做白名单 / 受保护目录比较，防止用 PROGRA~1 之类绕过。
            full = ExpandLongPath(full);
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

    /// <summary>
    /// 逐段检查路径（含目标本身）是否带有 <see cref="FileAttributes.ReparsePoint"/>。
    /// 符号链接 / junction 会让删除、粉碎操作作用到真实目标之外，调用方命中时必须跳过。
    /// 无法读取某一段属性时继续检查其余段，避免误伤正常的系统临时目录清理。
    /// </summary>
    public static bool HasReparsePointInPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return true;

            var segments = full[root.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
                {
                    // 该段不存在或无权限读取：不据此判定为安全，也不中断整条路径检查
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true; // 路径无法解析时保守拒绝
        }
    }

    /// <summary>用 Win32 GetLongPathName 把 8.3 短名展开为长名；失败时原样返回。</summary>
    private static string ExpandLongPath(string fullPath)
    {
        try
        {
            for (var capacity = 512; capacity <= 32768; capacity *= 2)
            {
                var buffer = new StringBuilder(capacity);
                var length = GetLongPathName(fullPath, buffer, (uint)buffer.Capacity);
                if (length == 0) return fullPath;
                if (length < buffer.Capacity) return buffer.ToString();
            }
            return fullPath;
        }
        catch (Exception)
        {
            return fullPath;
        }
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

    /// <summary>
    /// 检测路径中是否存在以点或空格结尾的段（不含 "." / ".." 这两个合法的相对段）。
    /// Windows 文件系统在解析时会丢弃段尾的点 / 空格，导致 "C:\Windows." 实际指向
    /// "C:\Windows"，因此这类路径必须在护栏阶段拒绝，不能进入删除 / 粉碎流程。
    /// </summary>
    private static bool HasTrailingDotOrSpace(string path)
    {
        var slashNormalized = path.Replace('/', '\\');
        var segments = slashNormalized.Split('\\', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0) continue;
            var last = segment[^1];
            if (last is not ('.' or ' ')) continue;
            // "." 与 ".." 是合法的相对路径段，单独放行
            if (segment is "." or "..") continue;
            return true;
        }
        return false;
    }
}
