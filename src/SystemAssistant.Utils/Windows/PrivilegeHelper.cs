using System.Diagnostics;
using System.Security.Principal;

namespace SystemAssistant.Utils.Windows;

/// <summary>管理员权限检测与按需提升。</summary>
public static class PrivilegeHelper
{
    private static readonly Lazy<bool> IsAdminLazy = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    /// <summary>当前进程是否具备管理员权限。</summary>
    public static bool IsAdministrator => IsAdminLazy.Value;

    /// <summary>
    /// 以管理员身份重新启动本程序。必须由用户在界面上主动调用（点击「提升权限」按钮），
    /// 程序不会自行静默提权。
    /// </summary>
    /// <param name="arguments">传递给新进程的命令行参数。</param>
    /// <returns>用户同意 UAC 并成功启动时返回 true；用户取消时返回 false。</returns>
    public static bool RestartElevated(string arguments = "")
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return false;

            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
                WorkingDirectory = AppContext.BaseDirectory
            };
            var process = Process.Start(psi);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户在 UAC 弹窗点击了「否」，属于正常的主动取消
            return false;
        }
    }
}
