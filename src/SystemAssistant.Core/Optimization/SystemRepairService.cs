using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Utils.Diagnostics;

namespace SystemAssistant.Core.Optimization;

/// <summary>系统映像修复：sfc /scannow 与 DISM /RestoreHealth，实时回传控制台输出。</summary>
public sealed class SystemRepairService : ISystemRepairService
{
    private readonly CommandRunner _runner = new();
    private readonly IOperationLogService _log;

    public SystemRepairService(IOperationLogService log)
    {
        _log = log;
        _runner.OutputReceived += line => OutputReceived?.Invoke(line);
    }

    public event Action<CommandOutputLine>? OutputReceived;

    public async Task<CommandExecutionResult> RunSfcScanNowAsync(CancellationToken token = default)
    {
        _log.Log(OperationType.SystemCommand, "执行系统修复", "sfc /scannow", true);
        var result = await _runner.RunAsync("sfc /scannow", token, asAdmin: true);
        _log.Log(OperationType.SystemCommand, "系统修复完成", $"sfc /scannow，退出码 {result.ExitCode}", result.Succeeded,
            result.Succeeded ? null : result.StandardError);
        return result;
    }

    public async Task<CommandExecutionResult> RunDismRestoreHealthAsync(CancellationToken token = default)
    {
        _log.Log(OperationType.SystemCommand, "执行映像修复", "DISM /Online /Cleanup-Image /RestoreHealth", true);
        var result = await _runner.RunAsync("DISM /Online /Cleanup-Image /RestoreHealth", token, asAdmin: true);
        _log.Log(OperationType.SystemCommand, "映像修复完成", $"DISM，退出码 {result.ExitCode}", result.Succeeded,
            result.Succeeded ? null : result.StandardError);
        return result;
    }
}
