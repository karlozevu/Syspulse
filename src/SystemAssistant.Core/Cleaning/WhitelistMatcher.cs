using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SystemAssistant.Models.Cleaning;

namespace SystemAssistant.Core.Cleaning;

/// <summary>
/// 清理白名单匹配器。支持目录、后缀、文件名三类规则；扫描与删除两个阶段都会调用，
/// 防止用户误把重要路径加入清理列表。
/// 安全原则：任何解析 / 正则异常或超时都保守返回 true（命中白名单 → 跳过不删），
/// 绝不因为白名单失效而误删。
/// </summary>
public sealed class WhitelistMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<WhitelistRule> _rules;
    private int _skippedCount;

    public WhitelistMatcher(IEnumerable<WhitelistRule>? rules)
    {
        _rules = (rules ?? Array.Empty<WhitelistRule>())
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern))
            .ToArray();
    }

    /// <summary>本次扫描中被白名单跳过的条目数。</summary>
    public int SkippedCount => Volatile.Read(ref _skippedCount);

    public void ResetCounter() => Interlocked.Exchange(ref _skippedCount, 0);

    /// <summary>路径是否命中白名单。</summary>
    public bool IsWhitelisted(string path, bool count = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var extension = Path.GetExtension(fileName);

            foreach (var rule in _rules)
            {
                var pattern = rule.Pattern.Trim().Replace('/', '\\');
                var matched = rule.Kind switch
                {
                    "Extension" => !string.IsNullOrEmpty(extension)
                                   && string.Equals(extension,
                                       pattern.StartsWith('.') ? pattern : "." + pattern,
                                       StringComparison.OrdinalIgnoreCase),
                    "FileName" => SegmentMatch(fileName, pattern),
                    // Directory：按路径分段做前缀 / 通配符匹配，不再用子串包含
                    _ => DirectoryMatch(path, pattern) || SegmentMatch(fileName, pattern)
                };
                if (!matched) continue;
                if (count) Interlocked.Increment(ref _skippedCount);
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            // 路径解析或匹配异常：保守视为命中白名单，跳过该条目，绝不误删
            if (count) Interlocked.Increment(ref _skippedCount);
            return true;
        }
    }

    /// <summary>
    /// 目录规则匹配：把路径与模式按 '\' 拆成段，逐段做完整（支持 * ? 通配符）比较。
    /// 绝对模式要求从根开始逐段前缀匹配；相对模式要求按段边界对齐连续匹配。
    /// 这样 "Important" 不会误命中 "ImportantBackup" 这类子串。
    /// </summary>
    private static bool DirectoryMatch(string path, string pattern)
    {
        var pathParts = SplitSegments(path);
        var patternParts = SplitSegments(pattern);
        if (pathParts.Count == 0 || patternParts.Count == 0) return false;

        if (Path.IsPathRooted(pattern))
        {
            if (pathParts.Count < patternParts.Count) return false;
            for (var i = 0; i < patternParts.Count; i++)
            {
                if (!SegmentMatch(pathParts[i], patternParts[i])) return false;
            }
            return true;
        }

        for (var start = 0; start <= pathParts.Count - patternParts.Count; start++)
        {
            var ok = true;
            for (var i = 0; i < patternParts.Count; i++)
            {
                if (!SegmentMatch(pathParts[start + i], patternParts[i])) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    private static List<string> SplitSegments(string path) =>
        path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>单段匹配：缓存预编译正则；异常 / 超时保守返回 true（跳过不删）。</summary>
    private static bool SegmentMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
            return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);

        try
        {
            var regex = RegexCache.GetOrAdd(pattern, static p =>
            {
                var regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return new Regex(regexPattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            });
            return regex.IsMatch(input);
        }
        catch (Exception)
        {
            // RegexMatchTimeoutException / ArgumentException 等：保守视为命中白名单，跳过不删
            return true;
        }
    }
}
