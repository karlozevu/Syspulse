using Microsoft.Win32;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Optimization;

namespace SystemAssistant.Core.Optimization;

/// <summary>
/// 简易系统调优。只提供开关的读取与「显式调用才写入」的接口，
/// 程序启动时不会读取后自动应用任何值；每个开关都带风险说明，由 UI 二次确认后才调用 Apply。
/// </summary>
public sealed class SystemTuningService : ISystemTuningService
{
    private readonly IOperationLogService _log;

    private static readonly IReadOnlyList<TuningItem> Definitions = new[]
    {
        new TuningItem
        {
            Id = "animations",
            Name = "系统窗口动画",
            Description = "开关窗口最小化 / 最大化动画。关闭后可略微提升低配机器流畅度。",
            RiskText = "低风险：仅影响视觉效果，随时可以恢复。",
            Risk = TuningRisk.Safe,
            RegistryPath = @"Control Panel\Desktop\WindowMetrics",
            ValueName = "MinAnimate",
            Hive = TuningRegistryHive.CurrentUser,
            DefaultValue = 1,
            EnabledValue = 1,
            DisabledValue = 0
        },
        new TuningItem
        {
            Id = "suggestions",
            Name = "开始菜单应用建议 / 广告推送",
            Description = "关闭开始菜单中的应用推荐与建议内容。",
            RiskText = "低风险：仅影响推荐内容展示。",
            Risk = TuningRisk.Safe,
            RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            ValueName = "Start_TrackProgs",
            Hive = TuningRegistryHive.CurrentUser,
            // Start_TrackProgs：1 = 跟踪程序 / 显示建议，0 = 关闭建议。
            // 本项是「关闭建议」开关，因此「开启该调优」必须写 0，不能写 1。
            DefaultValue = 1,
            EnabledValue = 0,
            DisabledValue = 1
        }
    };

    /// <summary>把 UI 无关的根键枚举映射到 Microsoft.Win32.RegistryHive。</summary>
    private static RegistryHive ToRegistryHive(TuningRegistryHive hive) => hive switch
    {
        TuningRegistryHive.LocalMachine => RegistryHive.LocalMachine,
        _ => RegistryHive.CurrentUser
    };

    public SystemTuningService(IOperationLogService log) => _log = log;

    public IReadOnlyList<TuningItem> GetTuningItems()
        => Definitions.Select(item => new TuningItem
        {
            Id = item.Id,
            Name = item.Name,
            Description = item.Description,
            RiskText = item.RiskText,
            Risk = item.Risk,
            RegistryPath = item.RegistryPath,
            ValueName = item.ValueName,
            Hive = item.Hive,
            DefaultValue = item.DefaultValue,
            EnabledValue = item.EnabledValue,
            DisabledValue = item.DisabledValue,
            CurrentValue = ReadValue(item)
        }).ToArray();

    /// <summary>写入调优开关。仅在 UI 完成二次确认后由 ViewModel 调用。</summary>
    public OperationResult Apply(TuningItem item, bool enabled)
    {
        var definition = Definitions.FirstOrDefault(d => d.Id == item.Id);
        if (definition is null) return OperationResult.Fail("未知的调优项");

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(definition.Hive), RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(definition.RegistryPath, writable: true);
            if (key is null) return OperationResult.Fail("无法打开注册表键，可能需要管理员权限");
            key.SetValue(definition.ValueName, enabled ? definition.EnabledValue : definition.DisabledValue,
                RegistryValueKind.DWord);
            _log.Log(OperationType.SystemTuning, "修改系统调优项",
                $"{definition.Name} → {(enabled ? "开启" : "关闭")}（{definition.RegistryPath}\\{definition.ValueName}）", true);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Log(OperationType.SystemTuning, "修改系统调优项", definition.Name, false, ex.Message);
            return OperationResult.Fail(ex.Message, ex);
        }
    }

    /// <summary>查看虚拟内存配置；修改入口由 UI 打开系统属性对话框，程序不直接写页面文件设置。</summary>
    public VirtualMemoryInfo GetVirtualMemoryInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var pagingFiles = key?.GetValue("PagingFiles") as string[] ?? Array.Empty<string>();
            // PagingFiles 为空、或任一页面文件配置为「0 0」（初始 / 最大值都为 0）
            // 都表示由 Windows 自动管理；仅按 Length==0 判断会漏掉「?:\pagefile.sys 0 0」。
            var automatic = pagingFiles.Length == 0 || pagingFiles.Any(p =>
            {
                var parts = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 && parts[1] == "0" && parts[2] == "0";
            });
            long initial = 0, maximum = 0;
            if (!automatic)
            {
                var parts = pagingFiles[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3
                    && long.TryParse(parts[1], out var initMb)
                    && long.TryParse(parts[2], out var maxMb))
                {
                    initial = initMb;
                    maximum = maxMb;
                }
            }
            return new VirtualMemoryInfo
            {
                IsAutomatic = automatic,
                InitialSizeMb = initial,
                MaximumSizeMb = maximum,
                Description = automatic
                    ? "由 Windows 自动管理所有驱动器的分页文件大小"
                    : $"分页文件：{string.Join("；", pagingFiles)}"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new VirtualMemoryInfo { IsAutomatic = true, Description = "读取失败：" + ex.Message };
        }
    }

    private static bool ReadValue(TuningItem item)
    {
        // 键 / 值不存在或读取失败时，按该项的 Windows 默认值推导开关状态，而不是恒返回 false。
        var effective = item.DefaultValue;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(item.Hive), RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(item.RegistryPath);
            var value = key?.GetValue(item.ValueName);
            if (value is not null) effective = Convert.ToInt32(value);
        }
        catch
        {
            effective = item.DefaultValue;
        }
        // 「开启」状态即当前原始值等于 EnabledValue（其余情况视为关闭 / 未配置）
        return effective == item.EnabledValue;
    }
}
