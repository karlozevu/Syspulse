using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Styling;
using SystemAssistant.Models.Config;

namespace SystemAssistant.App.Services;

/// <summary>主题服务：浅色 / 深色 / 跟随系统（Avalonia 的 Default 变体会跟随系统主题）。</summary>
public static class ThemeService
{
    /// <summary>主题被其它页面修改时通知主窗口同步下拉框状态。</summary>
    public static event Action<ThemeMode>? ThemeChanged;

    public static void Apply(ThemeMode mode)
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = mode switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
        ThemeChanged?.Invoke(mode);
    }

    /// <summary>获取主窗口（供弹窗作为 Owner）。</summary>
    public static Window? GetMainWindow()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
