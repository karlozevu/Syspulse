using System.Runtime.InteropServices;
using Avalonia;

namespace SystemAssistant.App;

/// <summary>
/// 程序入口。仅负责启动 Avalonia 应用，具体服务装配在 <see cref="App"/> 中完成。
/// </summary>
internal static class Program
{
    // 单实例互斥：防止多开导致配置 / 日志 / 备份目录并发写冲突。
    // "Local\" 前缀限定当前登录会话；提权进程带 --elevated 参数跳过检查
    // （提权时新旧实例会短暂并存，旧实例随即退出，不能因互斥挡住管理员窗口）。
    private const string MutexName = @"Local\SystemAssistant_SingleInstance";
    private static Mutex? _instanceMutex;
    private const string ElevatedArg = "--elevated";

    [STAThread]
    public static void Main(string[] args)
    {
        var skipMutex = args.Any(a => string.Equals(a, ElevatedArg, StringComparison.OrdinalIgnoreCase));
        if (!skipMutex)
        {
            _instanceMutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // 已有一个实例在运行：提示用户后安静退出，避免多实例并发写数据。
                ShowAlreadyRunning();
                return;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // 保持 mutex 引用到进程结束，防止被 GC 释放导致互斥失效。
        GC.KeepAlive(_instanceMutex);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void ShowAlreadyRunning()
    {
        try { MessageBoxW(IntPtr.Zero, "Syspulse 已在运行，请切换到已打开的窗口。", "Syspulse", 0x40); }
        catch { /* 无 UI 环境下静默退出即可 */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
