using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;

namespace SystemAssistant.App.Views;

/// <summary>
/// 独立悬浮小窗口：实时展示全局 CPU / 内存占用，可置顶，可一键隐藏 / 显示主窗口。
/// </summary>
public sealed class FloatingDashboardWindow : Window
{
    private readonly FloatingDashboardViewModel _viewModel = new();

    /// <summary>当前已打开的仪表盘实例（避免重复打开）。</summary>
    public static FloatingDashboardWindow? Current { get; private set; }

    public FloatingDashboardWindow()
    {
        Current = this;
        Title = LocalizationService.Instance.Get("Dashboard_Title");
        Width = 260;
        Height = 190;
        MinWidth = 240;
        MinHeight = 170;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(100, 100);
        DataContext = _viewModel;

        var title = new TextBlock { Text = Title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 };
        LocalizationService.Instance.LanguageChanged += (_, _) =>
            title.Text = LocalizationService.Instance.Get("Dashboard_Title");

        var topmost = new CheckBox { Content = LocalizationService.Instance.Get("Dashboard_Topmost"), FontSize = 11 };
        LocalizationService.Instance.LanguageChanged += (_, _) =>
            topmost.Content = LocalizationService.Instance.Get("Dashboard_Topmost");
        topmost.IsCheckedChanged += (_, _) => Topmost = topmost.IsChecked == true;

        var hideButton = new Button { FontSize = 11, Padding = new Thickness(8, 3) };
        void UpdateHideText() => hideButton.Content = LocalizationService.Instance.Get(
            ThemeService.GetMainWindow()?.IsVisible == true ? "Dashboard_HideMain" : "Dashboard_ShowMain");
        UpdateHideText();
        LocalizationService.Instance.LanguageChanged += (_, _) => UpdateHideText();
        hideButton.Click += (_, _) =>
        {
            var main = ThemeService.GetMainWindow();
            if (main is null) return;
            if (main.IsVisible) main.Hide();
            else { main.Show(); main.Activate(); }
            UpdateHideText();
        };

        var cpuLabel = new TextBlock { Text = "CPU", FontSize = 11, Opacity = 0.7 };
        var cpuValue = new TextBlock { FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        cpuValue.Bind(TextBlock.TextProperty, new Binding(nameof(FloatingDashboardViewModel.CpuText)));
        var cpuBar = CreateBar(nameof(FloatingDashboardViewModel.CpuUsage));

        var memLabel = new TextBlock { Text = "MEM", FontSize = 11, Opacity = 0.7 };
        var memValue = new TextBlock { FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        memValue.Bind(TextBlock.TextProperty, new Binding(nameof(FloatingDashboardViewModel.MemoryText)));
        var memBar = CreateBar(nameof(FloatingDashboardViewModel.MemoryUsage));

        var root = new StackPanel { Margin = new Thickness(14), Spacing = 5 };
        root.Children.Add(title);
        root.Children.Add(topmost);
        root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        root.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { cpuLabel, cpuValue }
        });
        Grid.SetColumn(cpuLabel, 0);
        Grid.SetColumn(cpuValue, 1);
        root.Children.Add(cpuBar);
        root.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { memLabel, memValue }
        });
        Grid.SetColumn(memLabel, 0);
        Grid.SetColumn(memValue, 1);
        root.Children.Add(memBar);
        root.Children.Add(hideButton);
        Content = root;
    }

    private static ProgressBar CreateBar(string path)
    {
        var bar = new ProgressBar { Height = 7, Minimum = 0, Maximum = 100, ShowProgressText = false };
        bar.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, new Binding(path));
        return bar;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Detach();
        if (ReferenceEquals(Current, this)) Current = null;
        var main = ThemeService.GetMainWindow();
        if (main is { IsVisible: false }) main.Show();
        base.OnClosed(e);
    }
}
