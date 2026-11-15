using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SystemAssistant.App.Services;

namespace SystemAssistant.App.Views.Dialogs;

/// <summary>
/// 通用确认弹窗。高危操作必须勾选「我已阅读风险提示」才能点击确认，
/// 且由 <see cref="DialogService"/> 再弹出一次最终确认，形成强制二次确认。
/// </summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? detail = null, bool requireAcknowledge = false,
        string? acknowledgeText = null)
    {
        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineSpacing = 4 });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            root.Children.Add(new ScrollViewer
            {
                MaxHeight = 190,
                Content = new TextBlock
                {
                    Text = detail,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    FontSize = 11,
                    Opacity = 0.8
                }
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
            Content = LocalizationService.Instance.Get("Common_Confirm"),
            Classes = { "accent" },
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        var cancelButton = new Button
        {
            Content = LocalizationService.Instance.Get("Common_Cancel"),
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        acknowledge.IsCheckedChanged += (_, _) =>
            okButton.IsEnabled = !requireAcknowledge || acknowledge.IsChecked == true;
        okButton.IsEnabled = !requireAcknowledge;

        okButton.Click += (_, _) => Close(true);
        cancelButton.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, okButton }
        };
        root.Children.Add(buttons);

        Content = root;
    }
}
