using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SystemAssistant.App.Services;
using SystemAssistant.Models.Processes;

namespace SystemAssistant.App.Views.Dialogs;

/// <summary>进程详情窗口：加载模块与打开的文件句柄。</summary>
public sealed class ProcessDetailWindow : Window
{
    public ProcessDetailWindow(ProcessSnapshot process,
        IReadOnlyList<ProcessModuleInfo> modules, IReadOnlyList<ProcessHandleInfo> handles)
    {
        AppIcon.Apply(this);
        Title = $"{process.Name} (PID {process.ProcessId})";
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new StackPanel { Margin = new Thickness(16, 12), Spacing = 4 };
        header.Children.Add(new TextBlock { Text = Title, FontSize = 17, FontWeight = FontWeight.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = $"{process.ExecutablePath}   |   CPU {process.CpuPercent:F1} %   |   " +
                   $"{LocalizationService.Instance.Get("Process_Threads")} {process.ThreadCount}   |   {process.UserName}",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        var moduleBox = new ListBox
        {
            ItemsSource = modules.Count == 0
                ? new[] { LocalizationService.Instance.Get("Process_DetailNoModules") }
                : modules.Select(m => $"{m.Name}   {m.FileName}   [{m.Version}]").ToArray(),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11
        };
        var handleBox = new ListBox
        {
            ItemsSource = handles.Count == 0
                ? new[] { LocalizationService.Instance.Get("Process_DetailNoHandles") }
                : handles.Select(h => $"0x{h.HandleValue:X}   {h.ObjectType}   {h.ObjectName}").ToArray(),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11
        };

        // TabItem.Header 用带 Loc.Key 的 TextBlock，语言切换时即时更新。
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = LocalizedHeader("Process_Modules"), Content = moduleBox },
                new TabItem { Header = LocalizedHeader("Process_Handles"), Content = handleBox }
            }
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(tabs);
        Content = root;
    }

    private static TextBlock LocalizedHeader(string key)
    {
        var text = new TextBlock { FontSize = 13 };
        Localize.SetKey(text, key);
        return text;
    }
}
