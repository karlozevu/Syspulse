using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;
using SystemAssistant.Core.Common;

namespace SystemAssistant.App.Views;

/// <summary>
/// 主窗口：左侧导航 + 顶部工具栏（主题 / 语言 / 权限 / 仪表盘）+ 内容区 + 状态栏。
/// 内容区根据当前 ViewModel 由 <see cref="PageViewLocator"/> 生成对应页面，页面之间互不耦合。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly ContentControl _contentHost = new();
    private readonly List<(NavigationItem Item, Button Button)> _navButtons = new();
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromArgb(45, 0, 120, 215));

    public MainWindow()
    {
        Title = "System Assistant";
        Width = 1280;
        Height = 820;
        MinWidth = 1060;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DataContext = _viewModel;

        var sidebar = BuildSidebar();
        var toolbar = BuildToolbar();
        var statusBar = BuildStatusBar();

        var mainArea = new DockPanel { Margin = new Thickness(0) };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        mainArea.Children.Add(toolbar);
        mainArea.Children.Add(statusBar);
        mainArea.Children.Add(new ScrollViewer
        {
            Content = _contentHost,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(mainArea, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(mainArea);
        Content = grid;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage)) UpdateContent();
            else if (e.PropertyName == nameof(MainWindowViewModel.SelectedKind)) HighlightNavigation();
        };
        UpdateContent();
        HighlightNavigation();
    }

    private Control BuildSidebar()
    {
        var panel = new StackPanel { Margin = new Thickness(12, 18), Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "System Assistant",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "v" + ServiceHub.Instance.Update.CurrentVersion,
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 0, 16)
        });

        string? lastGroup = null;
        foreach (var item in _viewModel.NavigationItems)
        {
            if (lastGroup != item.Group)
            {
                lastGroup = item.Group;
                var groupHeader = new TextBlock
                {
                    FontSize = 11,
                    Opacity = 0.55,
                    Margin = new Thickness(8, 12, 0, 4)
                };
                Localize.SetKey(groupHeader, item.Group);
                panel.Children.Add(groupHeader);
            }

            var label = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Localize.SetKey(label, item.LabelKey);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock
            {
                Text = item.Icon,
                FontSize = 15,
                Width = 20,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(label);

            var button = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 9),
                Command = _viewModel.NavigateCommand,
                CommandParameter = item
            };
            _navButtons.Add((item, button));
            panel.Children.Add(button);
        }

        var hint = new TextBlock
        {
            Text = "本工具不会联网上传硬件信息、扫描结果或文件列表",
            FontSize = 10,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 28, 8, 0)
        };
        panel.Children.Add(hint);

        return new Border
        {
            Child = panel,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))
        };
    }

    private Control BuildToolbar()
    {
        var title = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        Localize.SetKey(title, "App_Title");

        var themeCombo = UiFactory.LocalizedCombo(
            new[] { "Theme_System", "Theme_Light", "Theme_Dark" },
            _viewModel.ThemeIndex, index => _viewModel.ThemeIndex = index);
        var languageCombo = UiFactory.LocalizedCombo(
            new[] { "Lang_ZhCn", "Lang_EnUs" },
            _viewModel.LanguageIndex, index => _viewModel.LanguageIndex = index);

        var adminButton = new Button
        {
            Content = LocalizationService.Instance.Get(_viewModel.IsAdministrator
                ? "Common_AdminOk" : "Common_Elevate"),
            Command = _viewModel.ElevateCommand,
            IsEnabled = !_viewModel.IsAdministrator,
            Padding = new Thickness(10, 6),
            FontSize = 12
        };
        var dashboardButton = UiFactory.Button("Process_Dashboard", _viewModel.OpenDashboardCommand);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(20, 12)
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(themeCombo, 1);
        Grid.SetColumn(languageCombo, 2);
        Grid.SetColumn(adminButton, 3);
        Grid.SetColumn(dashboardButton, 4);
        grid.Children.Add(title);
        grid.Children.Add(themeCombo);
        grid.Children.Add(languageCombo);
        grid.Children.Add(adminButton);
        grid.Children.Add(dashboardButton);
        return grid;
    }

    private Control BuildStatusBar()
    {
        var status = new TextBlock { FontSize = 11, Opacity = 0.7 };
        status.Bind(TextBlock.TextProperty,
            new Binding(nameof(MainWindowViewModel.GlobalStatus)) { Source = _viewModel });
        return new Border
        {
            Child = status,
            Padding = new Thickness(20, 7),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128))
        };
    }

    private void UpdateContent()
    {
        _contentHost.Content = _viewModel.CurrentPage is null
            ? null
            : PageViewLocator.Create(_viewModel.CurrentPage);
    }

    private void HighlightNavigation()
    {
        foreach (var (item, button) in _navButtons)
            button.Background = item.Kind == _viewModel.SelectedKind ? SelectedBrush : Brushes.Transparent;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        ServiceHub.Instance.Dispose();
        base.OnClosed(e);
    }
}
