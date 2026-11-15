using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;

namespace SystemAssistant.App.Views;

/// <summary>
/// 独立悬浮小窗口：实时展示全局 CPU / 内存占用，可置顶，可一键隐藏 / 显示主窗口。
/// </summary>
public sealed class FloatingDashboardWindow : Window
{
    private readonly FloatingDashboardViewModel _viewModel = new();
    private readonly TextBlock _titleText;
    private readonly CheckBox _topmost;
    private readonly Button _hideButton;
    private readonly EventHandler _languageHandler;

    /// <summary>当前已打开的仪表盘实例（避免重复打开）。</summary>
    public static FloatingDashboardWindow? Current { get; private set; }

    public FloatingDashboardWindow()
    {
        AppIcon.Apply(this);
        Current = this;
        Width = 268;
        Height = 210;
        MinWidth = 240;
        MinHeight = 190;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(100, 100);
        Background = ThemeService.Brush("SaHeaderBrush");
        DataContext = _viewModel;
        Localize.SetKey(this, "Dashboard_Title");

        _titleText = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13 };
        _topmost = new CheckBox { FontSize = 11 };
        _hideButton = new Button { FontSize = 11, Padding = new Thickness(8, 3), Classes = { "subtle" } };

        _languageHandler = (_, _) => ApplyLanguage();
        LocalizationService.Instance.LanguageChanged += _languageHandler;
        ApplyLanguage();

        _topmost.IsCheckedChanged += (_, _) => Topmost = _topmost.IsChecked == true;
        _hideButton.Click += (_, _) =>
        {
            var main = ThemeService.GetMainWindow();
            if (main is null) return;
            if (main.IsVisible) main.Hide();
            else { main.Show(); main.Activate(); }
            ApplyLanguage();
        };

        var cpuLabel = new TextBlock { Text = "CPU", FontSize = 11, Classes = { "caption" } };
        var cpuValue = new TextBlock { Classes = { "value" }, HorizontalAlignment = HorizontalAlignment.Right };
        cpuValue.Bind(TextBlock.TextProperty, new Binding(nameof(FloatingDashboardViewModel.CpuText)));
        var cpuBar = CreateBar(nameof(FloatingDashboardViewModel.CpuUsage));

        var memLabel = new TextBlock { Text = "MEM", FontSize = 11, Classes = { "caption" } };
        var memValue = new TextBlock { Classes = { "value" }, HorizontalAlignment = HorizontalAlignment.Right };
        memValue.Bind(TextBlock.TextProperty, new Binding(nameof(FloatingDashboardViewModel.MemoryText)));
        var memBar = CreateBar(nameof(FloatingDashboardViewModel.MemoryUsage));

        var card = new Border { Classes = { "card" }, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) };
        var cardBody = new StackPanel { Spacing = 6 };
        cardBody.Children.Add(Row(cpuLabel, cpuValue));
        cardBody.Children.Add(cpuBar);
        cardBody.Children.Add(Row(memLabel, memValue));
        cardBody.Children.Add(memBar);
        card.Child = cardBody;

        var root = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        root.Children.Add(_titleText);
        root.Children.Add(card);
        root.Children.Add(_topmost);
        root.Children.Add(_hideButton);
        Content = root;
    }

    private static Grid Row(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private void ApplyLanguage()
    {
        _titleText.Text = LocalizationService.Instance.Get("Dashboard_Title");
        _topmost.Content = LocalizationService.Instance.Get("Dashboard_Topmost");
        _hideButton.Content = LocalizationService.Instance.Get(
            ThemeService.GetMainWindow()?.IsVisible == true ? "Dashboard_HideMain" : "Dashboard_ShowMain");
    }

    private static ProgressBar CreateBar(string path)
    {
        var bar = new ProgressBar { Height = 7, Minimum = 0, Maximum = 100, ShowProgressText = false,
            Foreground = ThemeService.Brush("SaAccentBrush") };
        bar.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, new Binding(path));
        return bar;
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalizationService.Instance.LanguageChanged -= _languageHandler;
        _viewModel.Detach();
        if (ReferenceEquals(Current, this)) Current = null;
        var main = ThemeService.GetMainWindow();
        if (main is { IsVisible: false }) main.Show();
        base.OnClosed(e);
    }
}
