using SystemAssistant.App.Resources;
using SystemAssistant.Models.Config;

namespace SystemAssistant.App.Services;

/// <summary>
/// 运行时多语言服务。切换语言只替换当前字典并发出事件，所有绑定 Loc.Key 的控件立即刷新，
/// 无需重启，也不会丢失当前页面状态。
/// </summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> LazyInstance = new(() => new LocalizationService());

    public static LocalizationService Instance => LazyInstance.Value;

    private LanguageMode _language = LanguageMode.ZhCn;

    private LocalizationService() { }

    public event EventHandler? LanguageChanged;

    public LanguageMode Language => _language;

    public IReadOnlyDictionary<string, string> CurrentResources =>
        _language == LanguageMode.EnUs ? Strings.EnUs : Strings.ZhCn;

    /// <summary>取本地化文本；缺失的键原样返回，方便开发时发现问题。</summary>
    public string Get(string key)
        => CurrentResources.TryGetValue(key, out var value) ? value : key;

    /// <summary>取带 string.Format 参数的本地化文本。</summary>
    public string Format(string key, params object[] args)
    {
        var template = Get(key);
        try { return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args); }
        catch (FormatException) { return template; }
    }

    public string this[string key] => Get(key);

    /// <summary>切换语言并立即通知所有界面元素。</summary>
    public void SetLanguage(LanguageMode language)
    {
        if (_language == language) return;
        _language = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
