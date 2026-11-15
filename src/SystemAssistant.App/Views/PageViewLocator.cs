using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.ViewModels;

namespace SystemAssistant.App.Views;

/// <summary>
/// ViewModel → View 的映射。页面全部用代码构建（保持 View 与业务逻辑解耦），
/// 每个页面只负责布局与绑定，业务动作一律通过命令调用 ViewModel / Core 服务。
/// </summary>
public static partial class PageViewLocator
{
    public static Control Create(ViewModelBase viewModel) => viewModel switch
    {
        OverviewViewModel vm => BuildOverviewPage(vm),
        HardwareViewModel vm => BuildHardwarePage(vm),
        CleanerViewModel vm => BuildCleanerPage(vm),
        OptimizationViewModel vm => BuildOptimizationPage(vm),
        ProcessViewModel vm => BuildProcessPage(vm),
        ToolboxViewModel vm => BuildToolboxPage(vm),
        SettingsViewModel vm => BuildSettingsPage(vm),
        LogsViewModel vm => BuildLogsPage(vm),
        _ => new TextBlock { Text = viewModel.GetType().Name }
    };

    /// <summary>统一页面外壳：标题、说明、状态栏、忙碌指示条。</summary>
    private static Control Shell(ViewModelBase vm, string titleKey, string? subtitleKey, Control body)
    {
        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 4), Spacing = 4 };
        header.Children.Add(UiFactory.Heading(titleKey));
        if (subtitleKey is not null) header.Children.Add(UiFactory.Muted(subtitleKey));

        var status = UiFactory.BoundText(vm, nameof(ViewModelBase.StatusMessage), muted: true);
        var busy = new ProgressBar { Height = 3, IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 0) };
        busy.Bind(Visual.IsVisibleProperty, new Binding(nameof(ViewModelBase.IsBusy)) { Source = vm });

        var footer = new StackPanel { Margin = new Thickness(24, 8, 24, 14), Spacing = 2 };
        footer.Children.Add(busy);
        footer.Children.Add(status);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(24, 4, 24, 8), Children = { body } },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        // 页面根的数据上下文是当前页面 ViewModel（会覆盖主窗口的 DataContext）
        root.DataContext = vm;
        return root;
    }

    /// <summary>生成一组紧凑的信息行卡片。</summary>
    private static Control InfoCard(string titleKey, params (string LabelKey, string BindingPath)[] rows)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var (labelKey, path) in rows)
            panel.Children.Add(UiFactory.InfoRow(labelKey, path));
        return UiFactory.Section(titleKey, panel);
    }

    private static TableColumn Col(string headerKey, double width, Func<object, string> value,
        string? sortKey = null, bool stretch = false)
        => new() { HeaderKey = headerKey, Width = width, GetValue = value, SortKey = sortKey, Stretch = stretch };

    /// <summary>把进度条与进度文本绑定到一个 ViewModel。</summary>
    private static Control ProgressBlock(object source, string percentPath, string textPath)
    {
        var bar = UiFactory.Progress(percentPath, source);
        var text = UiFactory.BoundText(source, textPath, muted: true);
        return new StackPanel { Spacing = 4, Children = { bar, text }, Margin = new Thickness(0, 4, 0, 8) };
    }
}
