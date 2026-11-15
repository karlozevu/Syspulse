using Avalonia;
using Avalonia.Controls;

namespace SystemAssistant.App.Services;

/// <summary>
/// 附加属性 Loc.Key：把控件文本绑定到语言资源键。
/// 语言切换时自动重新赋值，因此页面上所有静态文本都能即时切换而不需要重建窗口。
/// </summary>
public sealed class Localize
{
    public static readonly AttachedProperty<string> KeyProperty =
        AvaloniaProperty.RegisterAttached<Localize, AvaloniaObject, string>("Key");

    private static readonly List<(WeakReference<AvaloniaObject> Target, string Key)> Registered = new();
    private static readonly object Sync = new();

    static Localize()
    {
        KeyProperty.Changed.AddClassHandler<AvaloniaObject>((target, e) =>
        {
            if (e.NewValue is not string key || string.IsNullOrEmpty(key)) return;
            lock (Sync)
            {
                // 同一控件换 key（例如「开始 / 停止采集」）时替换旧注册，避免重复刷新与陈旧文案。
                Registered.RemoveAll(entry => entry.Target.TryGetTarget(out var existing)
                                              && ReferenceEquals(existing, target));
                Registered.Add((new WeakReference<AvaloniaObject>(target), key));
            }
            Apply(target, key);
        });
        LocalizationService.Instance.LanguageChanged += (_, _) => RefreshAll();
    }

    public static void SetKey(AvaloniaObject element, string value) => element.SetValue(KeyProperty, value);

    public static string GetKey(AvaloniaObject element) => element.GetValue(KeyProperty);

    private static void RefreshAll()
    {
        (WeakReference<AvaloniaObject> Target, string Key)[] snapshot;
        lock (Sync)
        {
            // 清理已被回收的控件，避免长期运行时持有无用引用
            Registered.RemoveAll(entry => !entry.Target.TryGetTarget(out _));
            snapshot = Registered.ToArray();
        }
        foreach (var (reference, key) in snapshot)
        {
            if (reference.TryGetTarget(out var target)) Apply(target, key);
        }
    }

    private static void Apply(AvaloniaObject target, string key)
    {
        var text = LocalizationService.Instance.Get(key);
        switch (target)
        {
            // SelectableTextBlock 继承自 TextBlock，这里一并覆盖；
            // 只处理「内容本身就是字符串」的控件，避免覆盖带图标的按钮。
            case TextBlock textBlock:
                textBlock.Text = text;
                break;
            case TextBox textBox:
                textBox.Watermark = text;
                break;
            // Window 继承自 ContentControl，必须放在它前面；窗口标题栏同样要即时切换语言。
            case Window window:
                window.Title = text;
                break;
            case ContentControl content when content.Content is null or string:
                content.Content = text;
                break;
        }
    }
}
