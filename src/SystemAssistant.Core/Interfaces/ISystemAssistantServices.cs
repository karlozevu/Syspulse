using SystemAssistant.Models.Cleaning;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Config;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Models.Processes;
using SystemAssistant.Models.Toolbox;

namespace SystemAssistant.Core.Interfaces;

// ---------------------------------------------------------------------------
// 所有接口都只出现 Models 中的实体与 .NET BCL 类型，
// 不含任何 Avalonia / WPF 类型，保证 UI 层可整体替换。
// ---------------------------------------------------------------------------

/// <summary>用户设置持久化。</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    void Update(Action<AppSettings> change);
    void Reload();
}

/// <summary>操作日志：删除、修改、系统调用行为全部记录。</summary>
public interface IOperationLogService
{
    void Log(OperationType type, string action, string detail, bool success, string? errorMessage = null);
    IReadOnlyList<OperationLogEntry> GetAll();
    void ExportTo(string path);
    void Clear();
}

/// <summary>硬件静态信息读取。</summary>
public interface IHardwareInfoService
{
    Task<SystemOverview> GetOverviewAsync(CancellationToken token = default);
    Task<CpuInfo> GetCpuAsync(CancellationToken token = default);
    Task<MemoryInfo> GetMemoryAsync(CancellationToken token = default);
    Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken token = default);
    Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken token = default);
    Task<BatteryInfo> GetBatteryAsync(CancellationToken token = default);
    Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken token = default);
}

/// <summary>传感器持续采集（后台线程，事件推送，绝不阻塞 UI）。</summary>
public interface ISensorMonitor
{
    event Action<SensorSnapshot>? SnapshotUpdated;
    event Action<ThresholdAlert>? ThresholdExceeded;

    bool IsRunning { get; }
    int RefreshIntervalMs { get; set; }
    IReadOnlyList<SensorReading> LatestReadings { get; }

    void Start();
    void Stop();
    void SetThresholds(IReadOnlyList<SensorThreshold> thresholds);
}

/// <summary>整机报告导出（TXT / Markdown / HTML）。</summary>
public interface IHardwareReportExporter
{
    string BuildPlainText(SystemOverview overview);
    string BuildMarkdown(SystemOverview overview);
    string BuildHtml(SystemOverview overview);
    string Export(SystemOverview overview, string directory, string format);
}

/// <summary>安全垃圾清理：扫描 → 用户勾选 → 删除，三段严格分离。</summary>
public interface ICleanService
{
    Task<ScanResult> ScanAsync(ScanOptions options, IProgress<string>? status = null, CancellationToken token = default);
    /// <summary>
    /// 执行删除。只会处理 IsChecked 为 true 的项；包含高风险项时必须传入
    /// highRiskConfirmed = true（由 UI 的二次确认弹窗置位），否则高风险项会被跳过。
    /// </summary>
    Task<CleanResult> CleanAsync(IReadOnlyList<ScanItem> confirmedItems, bool highRiskConfirmed,
        IProgress<CleanProgress>? progress = null, CancellationToken token = default);
    Task<RegistryBackupResultHolder> BackupRegistryAsync(string directory, CancellationToken token = default);
}

/// <summary>注册表备份结果的 UI 无关载体。</summary>
public sealed class RegistryBackupResultHolder
{
    public bool Success { get; init; }
    public string Directory { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>开机启动项管理。</summary>
public interface IStartupItemService
{
    Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken token = default);
    Task<OperationResult> SetEnabledAsync(StartupItem item, bool enabled, CancellationToken token = default);
    Task<OperationResult> DeleteAsync(StartupItem item, CancellationToken token = default);
}

/// <summary>Windows 服务管理。</summary>
public interface IWindowsServiceManager
{
    Task<IReadOnlyList<WindowsServiceInfo>> GetServicesAsync(CancellationToken token = default);
    Task<OperationResult> StartAsync(string serviceName, CancellationToken token = default);
    Task<OperationResult> StopAsync(string serviceName, CancellationToken token = default);
    Task<OperationResult> SetStartModeAsync(string serviceName, ServiceStartMode mode, CancellationToken token = default);
}

/// <summary>系统映像修复（sfc / DISM），实时控制台输出。</summary>
public interface ISystemRepairService
{
    event Action<CommandOutputLine>? OutputReceived;
    Task<CommandExecutionResult> RunSfcScanNowAsync(CancellationToken token = default);
    Task<CommandExecutionResult> RunDismRestoreHealthAsync(CancellationToken token = default);
}

/// <summary>磁盘维护：SSD 执行 TRIM，HDD 执行碎片整理。</summary>
public interface IDiskMaintenanceService
{
    event Action<CommandOutputLine>? OutputReceived;
    Task<CommandExecutionResult> MaintainAsync(int diskIndex, char driveLetter, DiskMediaType mediaType,
        bool analyzeOnly, CancellationToken token = default);
}

/// <summary>网络工具箱。</summary>
public interface INetworkToolService
{
    Task<CommandExecutionResult> FlushDnsAsync(CancellationToken token = default);
    Task<CommandExecutionResult> ResetWinsockAsync(CancellationToken token = default);
    Task<CommandExecutionResult> ResetTcpIpAsync(CancellationToken token = default);
    Task<NetworkLatencyResult> TestLatencyAsync(string host, CancellationToken token = default);
}

/// <summary>简易系统调优：只读枚举 + 显式确认后的写入，绝不自动应用。</summary>
public interface ISystemTuningService
{
    IReadOnlyList<TuningItem> GetTuningItems();
    OperationResult Apply(TuningItem item, bool enabled);
    VirtualMemoryInfo GetVirtualMemoryInfo();
}

/// <summary>实时进程采样。</summary>
public interface IProcessMonitor
{
    event Action<IReadOnlyList<ProcessSnapshot>>? SnapshotUpdated;
    int RefreshIntervalMs { get; set; }
    void Start();
    void Stop();
}

/// <summary>进程操作与详情。</summary>
public interface IProcessService
{
    Task<OperationResult> KillAsync(int pid, bool includeTree);
    IReadOnlyList<ProcessModuleInfo> GetModules(int pid);
    IReadOnlyList<ProcessHandleInfo> GetHandles(int pid);
}

/// <summary>独立工具箱：文件粉碎、大文件、重复文件。</summary>
public interface IToolboxService
{
    Task<int> ShredAsync(IReadOnlyList<string> files, ShredOptions options,
        IProgress<ShredProgress>? progress = null, CancellationToken token = default);
    Task<IReadOnlyList<FileEntry>> FindLargeFilesAsync(LargeFileScanOptions options,
        IProgress<string>? status = null, CancellationToken token = default);
    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(DuplicateScanOptions options,
        IProgress<string>? status = null, CancellationToken token = default);
}

/// <summary>启动时的版本更新检测（仅检测，不下载、不静默安装）。</summary>
public interface IUpdateCheckService
{
    /// <summary>当前程序版本（来自程序集信息）。</summary>
    string CurrentVersion { get; }

    Task<AppVersionInfo> CheckAsync(CancellationToken token = default);
}
