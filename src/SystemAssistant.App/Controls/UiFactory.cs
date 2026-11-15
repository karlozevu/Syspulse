using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;

namespace SystemAssistant.App.Controls;

/// <summary>
/// 代码构建 UI 的轻量工厂。所有静态文本都通过 Loc.Key 附加属性本地化，
/// 动态数值使用 Avalonia 数据绑定，保持 View / ViewModel 解耦。
/// </summary>
public static class UiFactory
{
    public static TextBlock Heading(string key, string className = "h1")
    {
        var text = new TextBlock { Classes = { className } };
        Localize.SetKey(text, key);
        return text;
    }

    public static TextBlock SubHeading(string key)
    {
        var text = new TextBlock { Classes = { "h2" } };
        Localize.SetKey(text, key);
        return text;
    }

    public static TextBlock Muted(string key)
    {
        var text = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        Localize.SetKey(text, key);
        return text;
    }

    public static TextBlock Label(string key)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        Localize.SetKey(text, key);
        return text;
    }

    public static TextBlock Plain(string text, bool muted = false)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        if (muted) block.Classes.Add("muted");
        return block;
    }

    public static Button Button(string key, ICommand? command, object? parameter = null, bool accent = false)
    {
        var button = new Button { Command = command, CommandParameter = parameter,
            Padding = new Thickness(12, 7), VerticalAlignment = VerticalAlignment.Center };
        if (accent) button.Classes.Add("accent");
        Localize.SetKey(button, key);
        return button;
    }

    public static Button Button(string key, Action onClick, bool accent = false)
        => Button(key, new RelayCommand(onClick), null, accent);

    public static Button LocalizedButton(string key, Action onClick)
        => Button(key, onClick);

    public static TextBox Input(string watermarkKey, string text = "")
    {
        var box = new TextBox { Text = text, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(watermarkKey)) Localize.SetKey(box, watermarkKey);
        return box;
    }

    public static CheckBox Check(string key, bool isChecked, Action<bool>? onChanged = null)
    {
        var check = new CheckBox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center };
        Localize.SetKey(check, key);
        if (onChanged is not null) check.IsCheckedChanged += (_, _) => onChanged(check.IsChecked == true);
        return check;
    }

    /// <summary>双向绑定到 bool 属性的复选框。</summary>
    public static CheckBox BoundCheck(string key, object source, string path)
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        Localize.SetKey(check, key);
        check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(path) { Source = source, Mode = BindingMode.TwoWay });
        return check;
    }

    /// <summary>双向绑定到字符串属性的输入框（与 BoundText 区分，避免重载歧义）。</summary>
    public static TextBox BoundInput(object source, string path, bool multiline = false, double height = 32)
    {
        var box = new TextBox
        {
            Width = double.NaN,
            MinWidth = 180,
            Height = height,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        box.Bind(TextBox.TextProperty, new Binding(path) { Source = source, Mode = BindingMode.TwoWay });
        return box;
    }

    /// <summary>本地化下拉框；项使用 ComboBoxItem + Loc.Key，语言切换时自动更新。</summary>
    public static ComboBox LocalizedCombo(IEnumerable<string> keys, int selectedIndex, Action<int> onChanged)
    {
        var combo = new ComboBox { MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in keys)
        {
            var item = new ComboBoxItem();
            Localize.SetKey(item, key);
            combo.Items.Add(item);
        }
        combo.SelectedIndex = selectedIndex;
        combo.SelectionChanged += (_, _) => onChanged(combo.SelectedIndex);
        return combo;
    }

    /// <summary>把 TextBlock 的文本绑定到某个 ViewModel 属性。</summary>
    public static TextBlock BoundText(object source, string path, bool muted = false, bool mono = false)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            FontSize = mono ? 12 : 14
        };
        if (muted) text.Classes.Add("muted");
        if (mono) text.Classes.Add("mono");
        text.Bind(TextBlock.TextProperty, new Binding(path) { Source = source });
        return text;
    }

    public static Border Card(Control content)
        => new() { Classes = { "card" }, Child = content, Margin = new Thickness(0, 0, 0, 12) };

    /// <summary>带本地化标题的卡片。</summary>
    public static Border Section(string titleKey, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SubHeading(titleKey));
        foreach (var child in children) panel.Children.Add(child);
        return Card(panel);
    }

    /// <summary>一行「标签 : 绑定值」。</summary>
    public static Grid InfoRow(string labelKey, string bindingPath, object? source = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Thickness(0, 2) };
        var label = Label(labelKey);
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var binding = new Binding(bindingPath);
        if (source is not null) binding.Source = source;
        value.Bind(TextBlock.TextProperty, binding);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>一行「标签 : 静态值」。</summary>
    public static Grid StaticRow(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Thickness(0, 2) };
        var key = new TextBlock { Text = label, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(key, 0);
        Grid.SetColumn(val, 1);
        grid.Children.Add(key);
        grid.Children.Add(val);
        return grid;
    }

    public static StackPanel Toolbar(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12) };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    public static TabControl Tabs(params (string HeaderKey, Control Content)[] tabs)
    {
        var control = new TabControl();
        foreach (var (headerKey, content) in tabs)
        {
            var header = new TextBlock();
            Localize.SetKey(header, headerKey);
            control.Items.Add(new TabItem { Header = header, Content = Scroll(new StackPanel { Children = { content }, Margin = new Thickness(0, 4) }) });
        }
        return control;
    }

    public static ProgressBar Progress(string bindingPath, object source)
    {
        var bar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, ShowProgressText = false };
        bar.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, new Binding(bindingPath) { Source = source });
        return bar;
    }

    public static FuncDataTemplate<object> DefaultTemplate(Func<object, Control> builder)
        => new((item, _) => item is null ? new Control() : builder(item));
}
