using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SystemAssistant.App.Views;
using SystemAssistant.Core.Common;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.Diagnostics;

namespace SystemAssistant.App;

/// <summary>
/// 应用外壳：初始化日志、装配 UI 服务并创建主窗口。
/// 业务服务的装配在 Core 层的 ServiceHub 中完成，本类不含业务逻辑。
/// </summary>
public partial class App : Application
{
    // 保持引用，避免日志提供器被 GC 回收
    private ILoggerFactory? _loggerFactory;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            _loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddDebug()
                .AddProvider(new FileLoggerProvider(AppPaths.DiagnosticsLogFile)));
        }
        catch (IOException)
        {
            // 日志目录不可写时不影响主程序运行
        }

        // 全局异常兜底：记录到本地日志，避免硬件读取异常导致整个进程静默退出
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _loggerFactory?.CreateLogger("Unhandled").LogError(e.ExceptionObject as Exception, "未处理的异常");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _loggerFactory?.CreateLogger("Unobserved").LogError(e.Exception, "未观察的任务异常");
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            // 全局服务单例跟随进程生命周期：仅在应用真正退出时释放一次。
            // 不能在 MainWindow.OnClosed 里释放——悬浮仪表盘可独立存在并把主窗口重新显示，
            // 那时单例已死会导致服务不可恢复。
            // 关键：ShutdownRequested 也可能在"退出被取消"时触发（主窗口 OnClosing 因悬浮窗
            // 可见而 Cancel → Hide，进程继续）。此时若立即 Dispose，服务全死但进程还活着，
            // 悬浮窗与主窗口的数据上下文都失效。因此延迟一个 UI 周期再确认：
            // 若仍有可见窗口说明退出被取消，跳过释放；进程真退出时由 OS 回收资源。
            desktop.ShutdownRequested += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (desktop.MainWindow?.IsVisible == true) return; // 退出被取消，窗口还活着
                    if (FloatingDashboardWindow.Current is { IsVisible: true }) return;
                    ServiceHub.Instance.Dispose();
                }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
