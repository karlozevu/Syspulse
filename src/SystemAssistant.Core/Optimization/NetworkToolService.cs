using System.Globalization;
using System.Text.RegularExpressions;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Utils.Diagnostics;

namespace SystemAssistant.Core.Optimization;

/// <summary>网络工具箱：刷新 DNS、重置 Winsock / TCP/IP、网络延迟测试。</summary>
public sealed class NetworkToolService : INetworkToolService
{
    private readonly IOperationLogService _log;

    public NetworkToolService(IOperationLogService log) => _log = log;

    public Task<CommandExecutionResult> FlushDnsAsync(CancellationToken token = default)
        => Run("ipconfig /flushdns", "刷新 DNS 缓存", false, token);

    public Task<CommandExecutionResult> ResetWinsockAsync(CancellationToken token = default)
        => Run("netsh winsock reset", "重置 Winsock", true, token);

    public Task<CommandExecutionResult> ResetTcpIpAsync(CancellationToken token = default)
        => Run("netsh int ip reset", "重置 TCP/IP 参数", true, token);

    private async Task<CommandExecutionResult> Run(string command, string action, bool needsAdmin, CancellationToken token)
    {
        var runner = new CommandRunner();
        _log.Log(OperationType.SystemCommand, action, command, true);
        var result = await runner.RunAsync(command, token, asAdmin: needsAdmin);
        _log.Log(OperationType.SystemCommand, action + "完成", $"退出码 {result.ExitCode}", result.Succeeded,
            result.Succeeded ? null : result.StandardError);
        if (command.Contains("reset", StringComparison.OrdinalIgnoreCase))
        {
            _log.Log(OperationType.SystemCommand, "提示", "网络重置通常需要重启计算机后生效", true);
        }
        return result;
    }

    /// <summary>用系统 ping 测量平均延迟与丢包率，兼容中英文输出。</summary>
    public async Task<NetworkLatencyResult> TestLatencyAsync(string host, CancellationToken token = default)
    {
        var target = string.IsNullOrWhiteSpace(host) ? "www.baidu.com" : host.Trim();
        // 防止命令注入：只允许域名 / IP 中合法字符；拒绝以 '-' 开头（会被 ping 当成开关），
        // 并限制长度不超过 DNS 允许的 253 字符。
        if (target.Length > 253 || target[0] == '-'
            || !Regex.IsMatch(target, @"^[A-Za-z0-9.\-:]+$"))
        {
            return new NetworkLatencyResult
            {
                Host = target,
                RawOutput = "目标主机不合法：长度超限、以 - 开头或包含非法字符"
            };
        }

        var runner = new CommandRunner();
        var result = await runner.RunAsync($"ping -n 4 {target}", token);
        var output = result.StandardOutput + result.StandardError;
        _log.Log(OperationType.SystemCommand, "网络延迟测试", $"{target}，退出码 {result.ExitCode}", result.Succeeded);

        var average = -1L;
        var loss = 100;

        var averageMatch = Regex.Match(output,
            @"(?:平均|Average|average)\s*[=:]\s*(\d+)\s*ms", RegexOptions.IgnoreCase);
        if (averageMatch.Success && long.TryParse(averageMatch.Groups[1].Value, out var ms)) average = ms;

        var lossMatch = Regex.Match(output, @"\((\d+)%\s*(?:丢失|loss|Lost)", RegexOptions.IgnoreCase);
        if (!lossMatch.Success)
            lossMatch = Regex.Match(output, @"(\d+)%\s*(?:丢失|packet loss|loss)", RegexOptions.IgnoreCase);
        if (lossMatch.Success && int.TryParse(lossMatch.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var lossValue)) loss = lossValue;
        else if (result.Succeeded) loss = 0;

        return new NetworkLatencyResult
        {
            Host = target,
            AverageLatencyMs = average,
            PacketLossPercent = loss,
            RawOutput = output
        };
    }
}
