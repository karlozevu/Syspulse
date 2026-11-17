using System.Diagnostics;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Processes;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.Core.Processes;

/// <summary>进程操作与详情查询。</summary>
public sealed class ProcessService : IProcessService
{
    private readonly IOperationLogService _log;

    public ProcessService(IOperationLogService log) => _log = log;

    public Task<OperationResult> KillAsync(int pid, bool includeTree)
    {
        return Task.Run(() =>
        {
            var action = includeTree ? "结束进程树" : "结束进程";
            try
            {
                using var process = Process.GetProcessById(pid);
                var name = process.ProcessName;
                if (includeTree) process.Kill(entireProcessTree: true);
                else process.Kill();
                _log.Log(OperationType.Process, action, $"{name} (PID {pid})", true);
                return OperationResult.Ok($"{name} (PID {pid}) 已结束");
            }
            catch (ArgumentException)
            {
                return OperationResult.Fail($"进程 {pid} 已退出");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                var message = "权限不足，无法结束该进程。请以管理员身份重启程序后重试：" + ex.Message;
                _log.Log(OperationType.Process, action, $"PID {pid}", false, message);
                return OperationResult.Fail(message, ex);
            }
            catch (AggregateException ex)
            {
                // Kill(entireProcessTree: true) 在树中部分进程无法终止时会抛 AggregateException，
                // 原 catch 链未覆盖会导致 Task faulted、操作日志缺失、界面拿不到结果。
                var detail = string.Join("；", ex.Flatten().InnerExceptions.Select(e => e.Message));
                var message = $"进程树中有部分进程未能结束：{detail}";
                _log.Log(OperationType.Process, action, $"PID {pid}", false, message);
                return OperationResult.Fail(message, ex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _log.Log(OperationType.Process, action, $"PID {pid}", false, ex.Message);
                return OperationResult.Fail(ex.Message, ex);
            }
        });
    }

    public IReadOnlyList<ProcessModuleInfo> GetModules(int pid)
    {
        var result = new List<ProcessModuleInfo>();
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessModule module in process.Modules)
            {
                try
                {
                    result.Add(new ProcessModuleInfo
                    {
                        Name = module.ModuleName,
                        FileName = module.FileName ?? string.Empty,
                        MemorySizeBytes = module.ModuleMemorySize,
                        Version = module.FileVersionInfo?.FileVersion ?? string.Empty
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return result.OrderBy(m => m.Name).ToArray();
    }

    public IReadOnlyList<ProcessHandleInfo> GetHandles(int pid)
        => WindowsNativeInfo.GetFileHandles(pid);
}
