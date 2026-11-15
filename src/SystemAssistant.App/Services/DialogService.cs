using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SystemAssistant.App.Views.Dialogs;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.App.Services;

/// <summary>
/// 弹窗与系统选择器服务。所有删除 / 注册表 / 服务 / 系统调优操作都必须经过这里：
/// 高危操作会连续弹出「风险确认 + 最后确认」两个对话框。
/// </summary>
public static class DialogService
{
    private static Window Owner => ThemeService.GetMainWindow()
        ?? throw new InvalidOperationException("主窗口尚未创建");

    /// <summary>普通确认（单次）。</summary>
    public static Task<bool> ConfirmAsync(string title, string message, string? detail = null)
        => new ConfirmDialog(title, message, detail).ShowDialog<bool>(Owner);

    /// <summary>
    /// 高危操作的强制二次确认：
    /// 第一次展示明细并要求勾选已阅读风险，第二次展示最终确认。
    /// 任一步取消都返回 false。
    /// </summary>
    public static async Task<bool> ConfirmHighRiskAsync(string actionDescription, string detail,
        string? checklist = null)
    {
        var loc = LocalizationService.Instance;
        var first = await new ConfirmDialog(
            loc.Get("Confirm_HighRiskTitle"),
            loc.Format("Optimize_ConfirmBody", actionDescription),
            string.IsNullOrWhiteSpace(checklist) ? detail : detail + Environment.NewLine + Environment.NewLine + checklist,
            requireAcknowledge: true).ShowDialog<bool>(Owner);
        if (!first) return false;

        return await new ConfirmDialog(
            loc.Get("Confirm_SecondTitle"),
            loc.Format("Confirm_SecondBody", actionDescription),
            detail,
            requireAcknowledge: true).ShowDialog<bool>(Owner);
    }

    public static Task MessageAsync(string title, string message)
        => new ConfirmDialog(title, message).ShowDialog<bool>(Owner);

    public static Task ErrorAsync(string message)
        => new ConfirmDialog(LocalizationService.Instance.Get("Message_ErrorTitle"), message).ShowDialog<bool>(Owner);

    /// <summary>选择文件夹，返回本机路径；取消返回 null。</summary>
    public static async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>选择多个文件。</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();
    }

    /// <summary>选择保存路径。</summary>
    public static async Task<string?> SaveFileAsync(string title, string suggestedFileName, string extension)
    {
        var clean = extension.TrimStart('.');
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(clean.ToUpperInvariant() + " 文件")
                {
                    Patterns = new[] { "*." + clean }
                }
            }
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>权限不足时引导提升权限（用户主动点击才会调用）。</summary>
    public static bool Elevate() => PrivilegeHelper.RestartElevated();
}
