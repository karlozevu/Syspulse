using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SystemAssistant.App.Services;

namespace SystemAssistant.App.Views.Dialogs;

/// <summary>
/// 通用确认弹窗。高危操作必须勾选「我已阅读风险提示」才能点击确认，
/// 且由 <see cref="DialogService"/> 再弹出一次最终确认，形成强制二次确认。
/// 高危弹窗用警示色竖条、卡片化详情与危险按钮，让风险等级在视觉上可辨。
/// </summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? detail = null, bool requireAcknowledge = false,
        string? acknowledgeText = null)
    {
        AppIcon.Apply(this);
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var highRisk = requireAcknowledge;
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };

        var marker = new Border
        {
            Width = 4,
            Height = 22,
            CornerRadius = new CornerRadius(2),
            Background = ThemeService.Brush(highRisk ? "SaDangerBrush" : "SaAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(highRisk ? "SaDangerBrush" : "SaAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(marker);
        header.Children.Add(titleBlock);
        root.Children.Add(header);
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineSpacing = 4
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailText = new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 11,
                Classes = { "muted" }
            };
            root.Children.Add(new Border
            {
                Classes = { "subtle" },
                Child = new ScrollViewer { MaxHeight = 190, Content = detailText }
            });
        }

        var acknowledge = new CheckBox
        {
            Content = acknowledgeText ?? LocalizationService.Instance.Get("Confirm_Acknowledge"),
            IsVisible = requireAcknowledge,
            IsChecked = false
        };
        root.Children.Add(acknowledge);

        var okButton = new Button
        {
            Classes = { highRisk ? "danger" : "accent" },
            MinWidth = 108,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Localize.SetKey(okButton, "Common_Confirm");
        var cancelButton = new Button
        {
            MinWidth = 108,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Localize.SetKey(cancelButton, "Common_Cancel");

        acknowledge.IsCheckedChanged += (_, _) =>
            okButton.IsEnabled = !requireAcknowledge || acknowledge.IsChecked == true;
        okButton.IsEnabled = !requireAcknowledge;

        okButton.Click += (_, _) => Close(true);
        cancelButton.Click += (_, _) => Close(false);

        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, okButton }
        });

        Content = root;
    }
}
