using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Logging;
using SystemAssistant.App.Views;
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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
