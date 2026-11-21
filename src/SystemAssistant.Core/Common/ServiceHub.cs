using SystemAssistant.Core.Cleaning;
using SystemAssistant.Core.Hardware;
using SystemAssistant.Core.Health;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Core.Optimization;
using SystemAssistant.Core.Processes;
using SystemAssistant.Core.Reports;
using SystemAssistant.Core.Toolbox;
using SystemAssistant.Models.Common;

namespace SystemAssistant.Core.Common;

/// <summary>
/// 组合根（Composition Root）：在此装配所有业务服务。
/// 位于 Core 层且不引用任何 UI 框架，UI 层只通过接口取用服务，
/// 因此把 Avalonia 换成 WPF 时只需重写 App 项目。
/// </summary>
public sealed class ServiceHub : IDisposable
{
    private static readonly Lazy<ServiceHub> LazyInstance = new(() => new ServiceHub());

    /// <summary>全局唯一实例（桌面应用单一进程）。</summary>
    public static ServiceHub Instance => LazyInstance.Value;

    private ServiceHub()
    {
        Settings = new SettingsService();
        OperationLog = new OperationLogService();
        Sensors = new SensorMonitor { RefreshIntervalMs = Settings.Current.ClampRefreshInterval() };
        Sensors.SetThresholds(Settings.Current.Thresholds);
        Hardware = new HardwareInfoService(Sensors);
        Reports = new HardwareReportExporter();
        Clean = new CleanService(Settings, OperationLog);
        Startup = new StartupItemService(OperationLog);
        Services = new WindowsServiceManager(OperationLog);
        Repair = new SystemRepairService(OperationLog);
        DiskMaintenance = new DiskMaintenanceService(OperationLog);
        Network = new NetworkToolService(OperationLog);
        Tuning = new SystemTuningService(OperationLog);
        Processes = new ProcessMonitor { RefreshIntervalMs = Settings.Current.ClampRefreshInterval() };
        ProcessService = new ProcessService(OperationLog);
        Toolbox = new ToolboxService(OperationLog);
        Update = new UpdateCheckService(() => Settings.Current.UpdateCheckUrl);
        AlertHistory = new AlertHistoryService();
        HealthCheck = new HealthCheckService(Hardware, Sensors);
        NetworkMonitor = new NetworkMonitorService();
        // 阈值告警落盘：弹窗由 UI 层订阅处理，历史记录在此统一接线，保证任何 UI 状态下都有记录
        Sensors.ThresholdExceeded += a => AlertHistory.Append(new SystemAssistant.Models.Health.AlertHistoryEntry
        {
            Timestamp = a.RaisedAt,
            SensorName = a.Reading.DisplayName,
            HardwareName = a.Reading.HardwareName,
            WatchKind = (SystemAssistant.Models.Health.SensorKind)(int)a.Reading.Kind,
            Value = a.Reading.Value,
            Threshold = a.Threshold,
            Unit = a.Reading.Unit
        });

        OperationLog.Log(OperationType.Security, "程序启动",
            $"版本 {Update.CurrentVersion}，管理员权限：{SystemAssistant.Utils.Windows.PrivilegeHelper.IsAdministrator}", true);
    }

    public ISettingsService Settings { get; }
    public IOperationLogService OperationLog { get; }
    public ISensorMonitor Sensors { get; }
    public SensorMonitor SensorMonitor => (SensorMonitor)Sensors;
    public IHardwareInfoService Hardware { get; }
    public IHardwareReportExporter Reports { get; }
    public ICleanService Clean { get; }
    public CleanService CleanService => (CleanService)Clean;
    public IStartupItemService Startup { get; }
    public IWindowsServiceManager Services { get; }
    public ISystemRepairService Repair { get; }
    public IDiskMaintenanceService DiskMaintenance { get; }
    public INetworkToolService Network { get; }
    public ISystemTuningService Tuning { get; }
    public IProcessMonitor Processes { get; }
    public IProcessService ProcessService { get; }
    public IToolboxService Toolbox { get; }
    public IUpdateCheckService Update { get; }
    public IAlertHistoryService AlertHistory { get; }
    public IHealthCheckService HealthCheck { get; }
    public INetworkMonitorService NetworkMonitor { get; }

    /// <summary>把设置页的修改同步到运行中的采集器（即时生效，无需重启）。</summary>
    public void ApplyRuntimeSettings()
    {
        var interval = Settings.Current.ClampRefreshInterval();
        Sensors.RefreshIntervalMs = interval;
        Processes.RefreshIntervalMs = interval;
        Sensors.SetThresholds(Settings.Current.Thresholds);
    }

    public void Dispose()
    {
        (Sensors as IDisposable)?.Dispose();
        (Processes as IDisposable)?.Dispose();
        NetworkMonitor.Dispose();
    }
}
