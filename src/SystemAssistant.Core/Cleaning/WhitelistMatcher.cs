using SystemAssistant.Models.Cleaning;

namespace SystemAssistant.Core.Cleaning;

/// <summary>
/// 清理白名单匹配器。支持目录、后缀、文件名三类规则；扫描与删除两个阶段都会调用，
/// 防止用户误把重要路径加入清理列表。
/// </summary>
public sealed class WhitelistMatcher
{
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
        var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var extension = Path.GetExtension(fileName);
        var normalized = path.Replace('/', '\\');

        foreach (var rule in _rules)
        {
            var pattern = rule.Pattern.Trim().Replace('/', '\\');
            var matched = rule.Kind switch
            {
                "Extension" => !string.IsNullOrEmpty(extension)
                               && extension.Equals(pattern.StartsWith('.') ? pattern : "." + pattern,
                                   StringComparison.OrdinalIgnoreCase),
                "FileName" => WildcardMatch(fileName, pattern),
                // Directory：路径中包含该目录片段，或路径以该目录为前缀
                _ => normalized.Contains(pattern.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                     || WildcardMatch(fileName, pattern)
            };
            if (!matched) continue;
            if (count) Interlocked.Increment(ref _skippedCount);
            return true;
        }
        return false;
    }

    /// <summary>支持 * 与 ? 的简单通配符匹配（大小写不敏感）。</summary>
    private static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(input, regex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
