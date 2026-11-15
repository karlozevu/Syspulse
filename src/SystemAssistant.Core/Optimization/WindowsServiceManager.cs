using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SystemAssistant.Core.Hardware;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Optimization;
using ServiceStartMode = SystemAssistant.Models.Optimization.ServiceStartMode;

namespace SystemAssistant.Core.Optimization;

/// <summary>
/// Windows 服务管理。状态用 ServiceController，启动类型用 WMI Win32_Service / 注册表。
/// 内置高风险服务名单，UI 必须展示 RiskHint 并二次确认后才能执行变更。
/// </summary>
public sealed class WindowsServiceManager : IWindowsServiceManager
{
    private readonly IOperationLogService _log;

    /// <summary>停止或禁用这些服务可能导致系统无法正常工作。</summary>
    private static readonly HashSet<string> HighRiskServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "DcomLaunch", "EventLog", "PlugPlay", "Power", "Winmgmt", "WinDefend",
        "CryptSvc", "LanmanWorkstation", "LanmanServer", "Netlogon", "Dhcp", "Dnscache",
        "Themes", "AudioSrv", "Audiosrv", "BFE", "MpsSvc", "wuauserv", "TrustedInstaller",
        "ProfSvc", "UserManager", "Schedule", "lmhosts", "nsi", "SamSs", "lsass"
    };

    private const string HighRiskHint =
        "高风险：该服务属于系统核心组件，停止或禁用可能导致无法登录、无网络或系统不稳定。除非明确知道后果，否则不要修改。";

    // 服务名只允许字母 / 数字 / 下划线 / 点 / 连字符，且显式拒绝路径分隔符，
    // 防止把带路径或 WQL 注入的 serviceName 传给 ServiceController / WMI / 注册表。
    private static readonly Regex ServiceNamePattern =
        new(@"^[A-Za-z0-9_.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public WindowsServiceManager(IOperationLogService log) => _log = log;

    public Task<IReadOnlyList<WindowsServiceInfo>> GetServicesAsync(CancellationToken token = default)
        => Task.Run(() => (IReadOnlyList<WindowsServiceInfo>)ReadServices(token), token);

    private IReadOnlyList<WindowsServiceInfo> ReadServices(CancellationToken token)
    {
        var modes = new Dictionary<string, (string Mode, bool Delayed, string Description)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiQuery.CimV2,
                "SELECT Name, StartMode, DelayedAutoStart, Description FROM Win32_Service");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;
                    modes[name] = (
                        mo["StartMode"]?.ToString() ?? string.Empty,
                        mo["DelayedAutoStart"] is true,
                        mo["Description"]?.ToString() ?? string.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException) { }

        var result = new List<WindowsServiceInfo>();
        foreach (var controller in ServiceController.GetServices())
        {
            if (token.IsCancellationRequested) break;
            using (controller)
            {
                try
                {
                    modes.TryGetValue(controller.ServiceName, out var modeInfo);
                    var highRisk = HighRiskServices.Contains(controller.ServiceName);
                    result.Add(new WindowsServiceInfo
                    {
                        ServiceName = controller.ServiceName,
                        DisplayName = controller.DisplayName,
                        Description = modeInfo.Description,
                        State = MapState(controller.Status),
                        StartMode = MapStartMode(modeInfo.Mode, modeInfo.Delayed),
                        IsHighRisk = highRisk,
                        RiskHint = highRisk ? HighRiskHint : string.Empty
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 服务在枚举期间被卸载
                }
            }
        }
        return result.OrderByDescending(s => s.IsHighRisk).ThenBy(s => s.DisplayName).ToArray();
    }

    // 说明：IWindowsServiceManager 的现有调用点（App 层）在修改前已由 UI 展示 RiskHint
    // 并完成二次确认弹窗，因此这里显式传入 highRiskConfirmed: true，保持既有行为不变；
    // 内部 ChangeState/SetStartMode 的默认值仍为 false，未确认时一律拒绝高风险服务。
    public Task<OperationResult> StartAsync(string serviceName, CancellationToken token = default) =>
        Task.Run(() => ChangeState(serviceName, true, highRiskConfirmed: true), token);

    public Task<OperationResult> StopAsync(string serviceName, CancellationToken token = default) =>
        Task.Run(() => ChangeState(serviceName, false, highRiskConfirmed: true), token);

    /// <summary>校验服务名：拒绝空值、路径分隔符、WQL 注入字符与非法字符。</summary>
    private static bool IsValidServiceName(string serviceName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            error = "服务名为空";
            return false;
        }
        if (serviceName.IndexOfAny(new[] { '\\', '/', '"', '\'', '`', ';' }) >= 0
            || serviceName.Contains("..", StringComparison.Ordinal))
        {
            error = "服务名包含路径分隔符或非法字符";
            return false;
        }
        if (serviceName.Length > 256 || !ServiceNamePattern.IsMatch(serviceName))
        {
            error = "服务名不合法：只允许字母、数字、下划线、点、连字符";
            return false;
        }
        return true;
    }

    /// <summary>把高风险提示文字附加到结果中，让 UI 始终能看到风险说明。</summary>
    private static OperationResult AppendRiskHint(OperationResult result, string serviceName)
    {
        if (!HighRiskServices.Contains(serviceName)) return result;
        var hint = "高风险服务：" + HighRiskHint;
        var message = string.IsNullOrEmpty(result.Message) ? hint : result.Message + "（" + hint + "）";
        return new OperationResult { Success = result.Success, Message = message, Exception = result.Exception };
    }

    private OperationResult ChangeState(string serviceName, bool start, bool highRiskConfirmed = false)
    {
        if (!IsValidServiceName(serviceName, out var invalid)) return OperationResult.Fail(invalid);
        var highRisk = HighRiskServices.Contains(serviceName);
        if (highRisk && !highRiskConfirmed)
            return OperationResult.Fail("高风险服务必须先完成二次确认：" + HighRiskHint);

        try
        {
            using var controller = new ServiceController(serviceName);
            if (start)
            {
                if (controller.Status == ServiceControllerStatus.Running)
                    return AppendRiskHint(OperationResult.Ok("服务已在运行"), serviceName);
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            else
            {
                if (controller.Status == ServiceControllerStatus.Stopped)
                    return AppendRiskHint(OperationResult.Ok("服务已停止"), serviceName);
                if (!controller.CanStop)
                    return AppendRiskHint(OperationResult.Fail("该服务不允许被停止"), serviceName);
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            _log.Log(OperationType.Service, start ? "启动服务" : "停止服务", serviceName, true);
            return AppendRiskHint(OperationResult.Ok(), serviceName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or System.ServiceProcess.TimeoutException)
        {
            _log.Log(OperationType.Service, start ? "启动服务" : "停止服务", serviceName, false, ex.Message);
            return AppendRiskHint(OperationResult.Fail(ex.Message, ex), serviceName);
        }
    }

    // 与 Start/Stop 一致：UI 已做二次确认，显式传入 highRiskConfirmed: true 保持既有调用点可用。
    public Task<OperationResult> SetStartModeAsync(string serviceName, ServiceStartMode mode, CancellationToken token = default)
        => Task.Run(() => SetStartMode(serviceName, mode, highRiskConfirmed: true), token);

    private OperationResult SetStartMode(string serviceName, ServiceStartMode mode, bool highRiskConfirmed = false)
    {
        if (!IsValidServiceName(serviceName, out var invalid)) return OperationResult.Fail(invalid);
        var highRisk = HighRiskServices.Contains(serviceName);
        if (highRisk && mode == ServiceStartMode.Disabled)
            return OperationResult.Fail("禁止将高风险服务设为禁用：" + HighRiskHint);
        if (highRisk && !highRiskConfirmed)
            return OperationResult.Fail("高风险服务必须先完成二次确认：" + HighRiskHint);

        var wmiMode = mode switch
        {
            ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayedStart => "Automatic",
            ServiceStartMode.Manual => "Manual",
            ServiceStartMode.Disabled => "Disabled",
            _ => null
        };
        if (wmiMode is null) return OperationResult.Fail("不支持的启动类型");

        try
        {
            using var searcher = new ManagementObjectSearcher(WmiQuery.CimV2,
                $"SELECT * FROM Win32_Service WHERE Name = '{serviceName.Replace("'", "''")}'");
            using var collection = searcher.Get();
            var found = false;
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    found = true;
                    var output = mo.InvokeMethod("ChangeStartMode", new object[] { wmiMode });
                    var returnValue = Convert.ToInt32(output);
                    if (returnValue != 0)
                    {
                        var message = $"ChangeStartMode 返回 {returnValue}（可能需要管理员权限）";
                        _log.Log(OperationType.Service, "修改启动类型", $"{serviceName} → {mode}", false, message);
                        return AppendRiskHint(OperationResult.Fail(message), serviceName);
                    }
                }
            }

            // WMI 没查到该服务时不能静默返回成功，否则用户会误以为已修改。
            if (!found)
            {
                var message = $"未找到服务 {serviceName}（WMI 未返回 Win32_Service 记录）";
                _log.Log(OperationType.Service, "修改启动类型", $"{serviceName} → {mode}", false, message);
                return OperationResult.Fail(message);
            }

            // 「自动（延迟启动）」由注册表的 DelayedAutoStart 附加标记控制
            if (mode == ServiceStartMode.AutomaticDelayedStart)
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                key?.SetValue("DelayedAutoStart", 1, RegistryValueKind.DWord);
            }
            else if (mode == ServiceStartMode.Automatic)
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                key?.SetValue("DelayedAutoStart", 0, RegistryValueKind.DWord);
            }

            _log.Log(OperationType.Service, "修改启动类型", $"{serviceName} → {mode}", true);
            return AppendRiskHint(OperationResult.Ok(), serviceName);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                      or System.Security.SecurityException or IOException)
        {
            _log.Log(OperationType.Service, "修改启动类型", $"{serviceName} → {mode}", false, ex.Message);
            return AppendRiskHint(OperationResult.Fail(ex.Message, ex), serviceName);
        }
    }

    private static ServiceState MapState(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => ServiceState.Stopped,
        ServiceControllerStatus.StartPending => ServiceState.StartPending,
        ServiceControllerStatus.StopPending => ServiceState.StopPending,
        ServiceControllerStatus.Running => ServiceState.Running,
        ServiceControllerStatus.ContinuePending => ServiceState.ContinuePending,
        ServiceControllerStatus.PausePending => ServiceState.PausePending,
        ServiceControllerStatus.Paused => ServiceState.Paused,
        _ => ServiceState.Unknown
    };

    private static ServiceStartMode MapStartMode(string? wmi, bool delayed) => wmi switch
    {
        "Auto" or "Automatic" => delayed ? ServiceStartMode.AutomaticDelayedStart : ServiceStartMode.Automatic,
        "Manual" => ServiceStartMode.Manual,
        "Disabled" => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Unknown
    };
}
