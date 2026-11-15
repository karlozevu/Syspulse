using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SystemAssistant.App.Services;

namespace SystemAssistant.App.Views.Dialogs;

/// <summary>内嵌控制台输出窗口：实时展示 sfc / DISM / defrag 等命令的执行日志。</summary>
public sealed class ConsoleWindow : Window
{
    private readonly TextBox _output;
    private int _lineCount;

    /// <param name="titleKey">本地化资源键（窗口标题随语言即时切换）。</param>
    public ConsoleWindow(string titleKey)
    {
        AppIcon.Apply(this);
        Width = 820;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Localize.SetKey(this, titleKey);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(20, 22, 26)),
            Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 230)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var close = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Classes = { "subtle" }
        };
        Localize.SetKey(close, "Common_Close");
        close.Click += (_, _) => Close();

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer
        {
            Content = _output,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        Content = root;
    }

    /// <summary>追加一行输出（可从后台线程调用，自动切换回 UI 线程）。</summary>
    public void Append(string line, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_lineCount > 5000)
            {
                _output.Text = string.Empty;
                _lineCount = 0;
            }
            var prefix = isError ? "[!] " : "    ";
            _output.Text += $"{DateTime.Now:HH:mm:ss} {prefix}{line}{Environment.NewLine}";
            _lineCount++;
            _output.CaretIndex = _output.Text?.Length ?? 0;
        });
    }

    public void AppendSeparator(string title)
        => Append("──────── " + title + " ────────");
}
