using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SystemAssistant.App.Services;

/// <summary>
/// 统一的应用图标。Avalonia 窗口图标从 avares 资源读取 PNG（Skia 解码最稳妥），
/// Win32 exe / 安装包图标由 csproj 的 ApplicationIcon 与 Inno Setup 的 SetupIconFile 使用 ICO。
/// 图标缺失时不影响程序运行，只退化为系统默认图标。
/// </summary>
public static class AppIcon
{
    private static WindowIcon? _cached;
    private static bool _attempted;

    /// <summary>取得缓存的窗口图标；加载失败返回 null。</summary>
    public static WindowIcon? Get()
    {
        if (_attempted) return _cached;
        _attempted = true;
        try
        {
            var uri = new Uri("avares://Syspulse/Assets/app-icon.png");
            using var stream = AssetLoader.Open(uri);
            _cached = new WindowIcon(stream);
        }
        catch (Exception)
        {
            // 图标属于装饰性资源，任何加载异常都不应阻止窗口显示
            _cached = null;
        }
        return _cached;
    }

    /// <summary>给主窗口、对话框和悬浮窗套用同一个图标。</summary>
    public static void Apply(Window window)
    {
        var icon = Get();
        if (icon is not null) window.Icon = icon;
    }

    /// <summary>
    /// 取侧栏/界面内使用的图标 Image。与窗口图标同源，
    /// 避免侧栏仍显示旧的 "SA" 文字块造成品牌断裂。加载失败返回 null。
    /// </summary>
    public static Image? GetImage()
    {
        try
        {
            var uri = new Uri("avares://Syspulse/Assets/app-icon.png");
            using var stream = AssetLoader.Open(uri);
            return new Image
            {
                Source = new Avalonia.Media.Imaging.Bitmap(stream),
                Width = 34,
                Height = 34,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
