using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;
using SystemAssistant.Models.Health;

namespace SystemAssistant.App.Views;

public static partial class PageViewLocator
{
    private static Control BuildHealthPage(HealthViewModel vm)
    {
        var checkTab = BuildCheckTab(vm);
        var alertTab = BuildAlertTab(vm);
        var networkTab = BuildNetworkTab(vm);
        var body = UiFactory.Tabs(
            ("Health_TabCheck", checkTab),
            ("Health_TabAlerts", alertTab),
            ("Health_TabNetwork", networkTab));
        return Shell(vm, "Health_Title", "Health_Subtitle", body);
    }

    private static Control BuildCheckTab(HealthViewModel vm)
    {
        var score = new TextBlock { FontSize = 46, FontWeight = Avalonia.Media.FontWeight.Bold };
        score.Bind(TextBlock.TextProperty, new Binding(nameof(HealthViewModel.ScoreText)));
        var scoreDetail = UiFactory.BoundText(vm, nameof(HealthViewModel.ScoreDetail));
        var summary = UiFactory.BoundText(vm, nameof(HealthViewModel.HealthSummary), muted: true);
        var scoreCard = UiFactory.Card(new StackPanel
        {
            Spacing = 4,
            Children = { score, scoreDetail, summary,
                UiFactory.Button("Health_Check", vm.CheckCommand, accent: true) }
        });

        var issueList = new ItemsControl();
        issueList.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(HealthViewModel.Issues)));
        issueList.ItemTemplate = UiFactory.DefaultTemplate(o =>
        {
            // 用绑定而非一次性赋值：切语言时 VM 就地更新 HealthIssueVm 文本，
            // 绑定会自动刷新，无需重建整个集合或重跑体检。
            var severity = new TextBlock { Width = 24 };
            severity.Bind(TextBlock.TextProperty, new Binding(nameof(HealthIssueVm.SeverityText)));
            var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            message.Bind(TextBlock.TextProperty, new Binding(nameof(HealthIssueVm.Message)));
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 4),
                Children = { severity, message }
            };
        });
        var issuesCard = UiFactory.Section("Health_Issues", issueList);

        return new StackPanel { Spacing = 10, Children = { scoreCard, issuesCard } };
    }

    private static Control BuildAlertTab(HealthViewModel vm)
    {
        var table = new TableView { Height = 420 };
        table.Configure(new[]
        {
            Col("Health_AlertTime", 170, o => ((AlertHistoryVm)o).Time),
            Col("Health_AlertSensor", 180, o => ((AlertHistoryVm)o).Sensor),
            Col("Health_AlertDetail", 420, o => ((AlertHistoryVm)o).Message, stretch: true)
        });
        // 告警历史在 ViewModel 构造时已加载；页面订阅后补一次填充
        table.SetItems(vm.Alerts);
        vm.RefreshAlertsRequested += () => table.SetItems(vm.Alerts);

        var toolbar = UiFactory.Toolbar(
            UiFactory.Button("Common_Refresh", vm.RefreshAlertsCommand),
            UiFactory.DangerButton("Common_Clear", vm.ClearAlertsCommand));
        return new StackPanel { Spacing = 8, Children = { toolbar, table } };
    }

    private static Control BuildNetworkTab(HealthViewModel vm)
    {
        var speed = new TextBlock { FontSize = 22 };
        speed.Bind(TextBlock.TextProperty, new Binding(nameof(HealthViewModel.NetworkSpeed)));
        var status = UiFactory.BoundText(vm, nameof(HealthViewModel.NetworkStatus), muted: true);
        var button = new Button { Command = vm.ToggleNetworkCommand, VerticalAlignment = VerticalAlignment.Center };
        button.Bind(Button.ContentProperty, new Binding(nameof(HealthViewModel.NetworkButtonText)));
        var controlCard = UiFactory.Card(new StackPanel
        {
            Spacing = 6,
            Children = { speed, status, button }
        });
        var hint = UiFactory.Muted("Health_NetworkHint");
        return new StackPanel { Spacing = 10, Children = { controlCard, hint } };
    }
}
