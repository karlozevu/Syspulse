namespace SystemAssistant.Models.Optimization;

/// <summary>启动项来源。</summary>
public enum StartupSource
{
    RegistryRun,
    RegistryRunOnce,
    StartupFolder,
    TaskScheduler
}

/// <summary>启动项信息。</summary>
public sealed class StartupItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public StartupSource Source { get; init; }
    public bool IsEnabled { get; init; }
    public string Location { get; init; } = string.Empty;
    public string RiskHint { get; init; } = string.Empty;
}

/// <summary>服务状态（与 ServiceControllerStatus 解耦，便于 UI 层独立展示）。</summary>
public enum ServiceState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
    Unknown
}

/// <summary>服务启动类型。</summary>
public enum ServiceStartMode
{
    Automatic,
    AutomaticDelayedStart,
    Manual,
    Disabled,
    Unknown
}

/// <summary>Windows 服务信息。</summary>
public sealed class WindowsServiceInfo
{
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ServiceState State { get; init; } = ServiceState.Unknown;
    public ServiceStartMode StartMode { get; init; } = ServiceStartMode.Unknown;
    public bool IsHighRisk { get; init; }

    /// <summary>高风险服务的文字提示，UI 必须展示。</summary>
    public string RiskHint { get; init; } = string.Empty;
}

/// <summary>调优项风险等级。</summary>
public enum TuningRisk
{
    Safe,
    Caution,
    Dangerous
}

/// <summary>调优项所在的注册表根键。避免用路径字符串推断 Hive（HKCU 的 Software 会被误判为 HKLM）。</summary>
public enum TuningRegistryHive
{
    CurrentUser,
    LocalMachine
}

/// <summary>系统调优开关项。任何修改都必须由用户主动确认，程序不会自动应用。</summary>
public sealed class TuningItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string RiskText { get; init; } = string.Empty;
    public TuningRisk Risk { get; init; } = TuningRisk.Caution;
    public bool CurrentValue { get; init; }
    public string RegistryPath { get; init; } = string.Empty;
    public string ValueName { get; init; } = string.Empty;

    /// <summary>显式指定根键，取代从 RegistryPath 字符串推断。</summary>
    public TuningRegistryHive Hive { get; init; } = TuningRegistryHive.CurrentUser;

    /// <summary>该注册表值在 Windows 中的默认值（键 / 值不存在时据此判断开关状态）。</summary>
    public int DefaultValue { get; init; }

    public int EnabledValue { get; init; } = 1;
    public int DisabledValue { get; init; }
}

/// <summary>命令行执行时的一行输出。</summary>
public sealed class CommandOutputLine
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Text { get; init; } = string.Empty;
    public bool IsError { get; init; }
}

/// <summary>命令执行结果。</summary>
public sealed class CommandExecutionResult
{
    public int ExitCode { get; init; } = -1;
    public bool Succeeded => ExitCode == 0;
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
}

/// <summary>网络延迟测试结果。</summary>
public sealed class NetworkLatencyResult
{
    public string Host { get; init; } = string.Empty;
    public long AverageLatencyMs { get; init; } = -1;
    public int PacketLossPercent { get; init; } = 100;
    public string RawOutput { get; init; } = string.Empty;
}

/// <summary>虚拟内存配置。</summary>
public sealed class VirtualMemoryInfo
{
    public bool IsAutomatic { get; init; }
    public long InitialSizeMb { get; init; }
    public long MaximumSizeMb { get; init; }
    public string Description { get; init; } = string.Empty;
}
