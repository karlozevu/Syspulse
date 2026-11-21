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
using SystemAssistant.Models.Config;

namespace SystemAssistant.App.Views;

/// <summary>
/// 主窗口：左侧导航 + 顶部工具栏（主题 / 语言 / 权限 / 仪表盘）+ 内容区 + 状态栏。
/// 页面 View 按 ViewModel 缓存，避免每次导航都重建视图、重复订阅事件造成泄漏。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly ContentControl _contentHost = new();
    private readonly List<(NavigationItem Item, Button Button)> _navButtons = new();
    private readonly Dictionary<ViewModelBase, Control> _viewCache = new();
    private ComboBox? _themeCombo;
    private ComboBox? _languageCombo;
    private Action<ThemeMode>? _themeHandler;
    private EventHandler? _languageHandler;

    public MainWindow()
    {
        AppIcon.Apply(this);
        Width = 1280;
        Height = 820;
        MinWidth = 1060;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DataContext = _viewModel;
        Localize.SetKey(this, "App_Title");

        var sidebar = BuildSidebar();
        var toolbar = BuildToolbar();
        var statusBar = BuildStatusBar();

        var mainArea = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        mainArea.Children.Add(toolbar);
        mainArea.Children.Add(statusBar);
        // 全窗口只保留这一层垂直滚动；页面内部不再各自嵌套 ScrollViewer。
        mainArea.Children.Add(new ScrollViewer
        {
            Content = _contentHost,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"),
            Background = ThemeService.Brush("SaSidebarBrush") };
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
        var panel = new StackPanel { Margin = new Thickness(12, 18), Spacing = 3 };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            Margin = new Thickness(6, 0, 0, 18) };
        // 侧栏品牌优先用真实心电图标，与窗口/任务栏/安装包保持一致；
        // 图标资源加载失败时回退到旧的 SA 文字块，不影响界面可用性。
        var brandIcon = AppIcon.GetImage();
        if (brandIcon is not null)
        {
            brand.Children.Add(brandIcon);
        }
        else
        {
            brand.Children.Add(new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9),
                Background = ThemeService.Brush("SaAccentBrush"),
                Child = new TextBlock
                {
                    Text = "SA", Foreground = Brushes.White, FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        var brandText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        brandText.Children.Add(new TextBlock { Text = "Syspulse", FontSize = 14,
            FontWeight = FontWeight.SemiBold });
        brandText.Children.Add(new TextBlock { Text = "v" + ServiceHub.Instance.Update.CurrentVersion,
            FontSize = 11, Classes = { "caption" } });
        brand.Children.Add(brandText);
        panel.Children.Add(brand);

        string? lastGroup = null;
        foreach (var item in _viewModel.NavigationItems)
        {
            if (lastGroup != item.Group)
            {
                lastGroup = item.Group;
                var groupHeader = new TextBlock
                {
                    Classes = { "caption" },
                    Margin = new Thickness(10, 14, 0, 5)
                };
                Localize.SetKey(groupHeader, item.Group);
                panel.Children.Add(groupHeader);
            }

            var label = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Localize.SetKey(label, item.LabelKey);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new TextBlock
            {
                Text = item.Icon,
                FontSize = 15,
                Width = 20,
                Foreground = ThemeService.Brush("SaAccentBrush"),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(label);

            var button = new Button
            {
                Classes = { "nav" },
                Content = row,
                Command = _viewModel.NavigateCommand,
                CommandParameter = item
            };
            UiFactory.AttachTooltip(button, item.LabelKey);
            _navButtons.Add((item, button));
            panel.Children.Add(button);
        }

        var hint = new TextBlock
        {
            Classes = { "caption" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 30, 10, 0)
        };
        Localize.SetKey(hint, "App_PrivacyHint");
        panel.Children.Add(hint);

        return new Border
        {
            Child = panel,
            Background = ThemeService.Brush("SaSidebarBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = ThemeService.Brush("SaDividerBrush")
        };
    }

    private Control BuildToolbar()
    {
        var title = new TextBlock { FontSize = 17, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        Localize.SetKey(title, "App_Title");
        var accentMark = new Border
        {
            Width = 4, Height = 20, CornerRadius = new CornerRadius(2),
            Background = ThemeService.Brush("SaAccentBrush"),
            Margin = new Thickness(0, 0, 10, 0)
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(accentMark);
        titleRow.Children.Add(title);

        _themeCombo = UiFactory.LocalizedCombo(
            new[] { "Theme_System", "Theme_Light", "Theme_Dark" },
            _viewModel.ThemeIndex, index => _viewModel.ThemeIndex = index);
        _languageCombo = UiFactory.LocalizedCombo(
            new[] { "Lang_ZhCn", "Lang_EnUs" },
            _viewModel.LanguageIndex, index => _viewModel.LanguageIndex = index);
        UiFactory.AttachTooltip(_themeCombo, "Theme_System");
        UiFactory.AttachTooltip(_languageCombo, "Lang_ZhCn");

        // 设置页 / 其它入口修改主题与语言时，顶部下拉框同步，且不会形成回环
        // （setter 中 Set 遇到相同值会直接返回 false）。
        _themeHandler = mode =>
        {
            if (_themeCombo is not null) _themeCombo.SelectedIndex = (int)mode;
        };
        _languageHandler = (_, _) =>
        {
            if (_languageCombo is not null)
                _languageCombo.SelectedIndex = (int)LocalizationService.Instance.Language;
        };
        ThemeService.ThemeChanged += _themeHandler;
        LocalizationService.Instance.LanguageChanged += _languageHandler;

        var adminButton = new Button
        {
            Command = _viewModel.ElevateCommand,
            IsEnabled = !_viewModel.IsAdministrator,
            Classes = { "subtle" }
        };
        Localize.SetKey(adminButton, _viewModel.IsAdministrator ? "Common_AdminOk" : "Common_Elevate");
        UiFactory.AttachTooltip(adminButton, "Common_AdminRequired");

        var dashboardButton = UiFactory.Button("Process_Dashboard", _viewModel.OpenDashboardCommand,
            variant: ButtonVariant.Subtle, tooltipKey: "Process_Dashboard");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(24, 14)
        };
        Grid.SetColumn(titleRow, 0);
        Grid.SetColumn(_themeCombo, 1);
        Grid.SetColumn(_languageCombo, 2);
        Grid.SetColumn(adminButton, 3);
        Grid.SetColumn(dashboardButton, 4);
        grid.Children.Add(titleRow);
        grid.Children.Add(_themeCombo);
        grid.Children.Add(_languageCombo);
        grid.Children.Add(adminButton);
        grid.Children.Add(dashboardButton);

        return new Border
        {
            Child = grid,
            Background = ThemeService.Brush("SaHeaderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ThemeService.Brush("SaDividerBrush")
        };
    }

    private Control BuildStatusBar()
    {
        var status = new TextBlock { FontSize = 11, Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center };
        status.Bind(TextBlock.TextProperty,
            new Binding(nameof(MainWindowViewModel.GlobalStatus)) { Source = _viewModel });

        var chipText = new TextBlock
        {
            Text = "v" + ServiceHub.Instance.Update.CurrentVersion,
            FontSize = 11,
            Foreground = ThemeService.Brush("SaAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var chip = new Border { Classes = { "chip" }, Child = chipText,
            VerticalAlignment = VerticalAlignment.Center };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(24, 8) };
        Grid.SetColumn(status, 0);
        Grid.SetColumn(chip, 1);
        grid.Children.Add(status);
        grid.Children.Add(chip);

        return new Border
        {
            Child = grid,
            Background = ThemeService.Brush("SaHeaderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = ThemeService.Brush("SaDividerBrush")
        };
    }

    private void UpdateContent()
    {
        var page = _viewModel.CurrentPage;
        if (page is null)
        {
            _contentHost.Content = null;
            return;
        }
        if (!_viewCache.TryGetValue(page, out var view))
        {
            view = PageViewLocator.Create(page);
            _viewCache[page] = view;
        }
        _contentHost.Content = view;
    }

    private void HighlightNavigation()
    {
        foreach (var (item, button) in _navButtons)
        {
            button.Classes.Remove("navSelected");
            if (item.Kind == _viewModel.SelectedKind) button.Classes.Add("navSelected");
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 悬浮仪表盘可以独立存在，并能通过「显示主窗口」把本窗口重新 Show()。
        // 若此时真正销毁主窗口：OnClosed 会 Dispose 全局单例与 ViewModel，重开的窗口
        // 将带着已释放的数据上下文且无法恢复。因此悬浮窗仍在时把「关闭」改为「隐藏」，
        // 让进程与所有服务保持完整；没有悬浮窗时按正常关闭退出。
        if (e.CloseReason is WindowCloseReason.WindowClosing
            && FloatingDashboardWindow.Current is { IsVisible: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_themeHandler is not null) ThemeService.ThemeChanged -= _themeHandler;
        if (_languageHandler is not null) LocalizationService.Instance.LanguageChanged -= _languageHandler;
        _viewModel.Dispose();
        // 不在此处 Dispose ServiceHub：全局单例跟随进程生命周期，由 App.Exit 统一释放。
        // 之前在这里释放会导致「悬浮窗仍开、主窗口被隐藏后又重新显示」时服务已死。
        _viewCache.Clear();
        base.OnClosed(e);
    }
}
