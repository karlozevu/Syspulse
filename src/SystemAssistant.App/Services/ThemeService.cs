using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Win32;
using SystemAssistant.Models.Config;

namespace SystemAssistant.App.Services;

/// <summary>
/// 主题服务：浅色 / 深色 / 跟随系统。
/// 除了切换 Avalonia 的 ThemeVariant，还维护一套自定义调色板（App.axaml 中定义的
/// Sa* 画刷），让卡片、表格、按钮、危险提示在深色主题下同样有足够对比度。
/// </summary>
public static class ThemeService
{
    /// <summary>主题或调色板被修改时通知界面同步。</summary>
    public static event Action<ThemeMode>? ThemeChanged;

    private static bool _hooked;

    /// <summary>浅色 / 深色两套取色表，键与 App.axaml 中的 x:Key 一一对应。</summary>
    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("SaAccentBrush",       "#0F6CBD", "#4CC2FF"),
        ("SaAccentSoftBrush",   "#E5F1FC", "#1B3040"),
        ("SaWarnBrush",         "#93620A", "#F2C94C"),
        ("SaWarnSoftBrush",     "#FFF4D6", "#3A3218"),
        ("SaDangerBrush",       "#C42B1C", "#FF8A80"),
        ("SaDangerSoftBrush",   "#FDECEA", "#3A2222"),
        ("SaSuccessBrush",      "#0E7A3C", "#6CCB8F"),
        ("SaSelectionBrush",    "#D8EAFB", "#27445C"),
        ("SaHoverBrush",        "#E9F1F9", "#2E3238"),
        ("SaRowAltBrush",       "#F6F8FB", "#26282C"),
        ("SaDividerBrush",      "#E1E7EF", "#3C4046"),
        ("SaCardBorderBrush",   "#DEE5EE", "#3A3E44"),
        ("SaHeaderBrush",       "#F1F5FA", "#2A2D32"),
        ("SaSidebarBrush",      "#F8FAFD", "#202327"),
        ("SaMutedTextBrush",    "#5C6675", "#A8B2BF")
    };

    public static void Apply(ThemeMode mode)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        // Default 变体由 Avalonia 跟随系统，这里同步读取 Windows 的应用明暗设置。
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark()
        };
        ApplyPalette(dark);
        HookSystemThemeChanges();
        ThemeChanged?.Invoke(mode);
    }

    /// <summary>运行期间系统主题变化时，跟随系统模式也能即时换色。</summary>
    private static void HookSystemThemeChanges()
    {
        if (_hooked || Application.Current is null) return;
        _hooked = true;
        Application.Current.ActualThemeVariantChanged += (_, _) =>
        {
            var app = Application.Current;
            if (app is null) return;
            ApplyPalette(app.ActualThemeVariant == ThemeVariant.Dark);
            ThemeChanged?.Invoke(ThemeMode.System);
        };
    }

    private static void ApplyPalette(bool dark)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        foreach (var (key, light, darkValue) in Palette)
        {
            var color = Color.Parse(dark ? darkValue : light);
            if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
                brush.Color = color;                      // 原地改色，DynamicResource 会即时刷新
            else
                resources[key] = new SolidColorBrush(color);
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>供代码构建的控件取当前调色板画刷。</summary>
    public static IBrush Brush(string key)
        => Application.Current?.Resources.TryGetValue(key, out var value) == true
           && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    /// <summary>获取主窗口（供弹窗作为 Owner）。</summary>
    public static Window? GetMainWindow()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
