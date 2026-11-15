using Avalonia;

namespace SystemAssistant.App;

/// <summary>
/// 程序入口。仅负责启动 Avalonia 应用，具体服务装配在 <see cref="App"/> 中完成。
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
