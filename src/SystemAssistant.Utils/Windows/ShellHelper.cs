using System.Diagnostics;

namespace SystemAssistant.Utils.Windows;

/// <summary>系统快捷入口与进程打开动作。</summary>
public static class ShellHelper
{
    /// <summary>一键打开系统内置工具。</summary>
    public static bool OpenSystemTool(string tool)
    {
        var psi = tool.ToLowerInvariant() switch
        {
            "taskmgr" => new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true },
            "regedit" => new ProcessStartInfo("regedit.exe") { UseShellExecute = true },
            "cmd" => new ProcessStartInfo("cmd.exe") { UseShellExecute = true },
            "powershell" => new ProcessStartInfo("powershell.exe") { UseShellExecute = true },
            "services" => new ProcessStartInfo("services.msc") { UseShellExecute = true },
            "msconfig" => new ProcessStartInfo("msconfig.exe") { UseShellExecute = true },
            // 性能选项 → 高级 → 虚拟内存（不直接改写页面文件设置，交给系统自带对话框）
            "virtualmemory" => new ProcessStartInfo("SystemPropertiesPerformance.exe") { UseShellExecute = true },
            _ => null
        };
        if (psi is null) return false;
        try
        {
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>使用系统默认程序打开文件。</summary>
    public static bool OpenFile(string path)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
