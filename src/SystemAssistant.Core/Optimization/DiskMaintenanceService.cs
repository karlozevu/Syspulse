using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Utils.Diagnostics;

namespace SystemAssistant.Core.Optimization;

/// <summary>
/// 磁盘维护：SSD 执行 TRIM（defrag /L），机械硬盘执行碎片整理（defrag /O）。
/// /A 仅分析不改动。所有命令都需要管理员权限，由 UI 二次确认后触发。
/// </summary>
public sealed class DiskMaintenanceService : IDiskMaintenanceService
{
    private readonly CommandRunner _runner = new();
    private readonly IOperationLogService _log;

    public DiskMaintenanceService(IOperationLogService log)
    {
        _log = log;
        _runner.OutputReceived += line => OutputReceived?.Invoke(line);
    }

    public event Action<CommandOutputLine>? OutputReceived;

    public async Task<CommandExecutionResult> MaintainAsync(int diskIndex, char driveLetter, DiskMediaType mediaType,
        bool analyzeOnly, CancellationToken token = default)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        if (!char.IsLetter(letter))
            return new CommandExecutionResult { ExitCode = -1, StandardError = "无效的盘符" };

        // /A 分析；/L 对 SSD 重新执行 TRIM；/O 对 HDD 按介质类型优化
        var option = analyzeOnly ? "/A"
            : mediaType == DiskMediaType.SolidStateDrive ? "/L"
            : "/O";
        var command = $"defrag {letter}: {option} /U /V";
        var action = analyzeOnly ? "分析磁盘" : mediaType == DiskMediaType.SolidStateDrive ? "SSD TRIM" : "HDD 碎片整理";

        _log.Log(OperationType.SystemCommand, action, $"磁盘 {diskIndex}，命令 {command}", true);
        var result = await _runner.RunAsync(command, token, asAdmin: true);
        _log.Log(OperationType.SystemCommand, action + "完成", $"退出码 {result.ExitCode}", result.Succeeded,
            result.Succeeded ? null : result.StandardError);
        return result;
    }
}
