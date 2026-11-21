using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Health;

namespace SystemAssistant.Core.Health;

/// <summary>
/// 系统健康体检：汇总 CPU / 内存 / 磁盘 SMART / 温度等实时与静态指标，
/// 生成 0~100 评分与问题清单。只读，不修改任何系统设置。
/// </summary>
public sealed class HealthCheckService : IHealthCheckService
{
    private readonly IHardwareInfoService _hardware;
    private readonly ISensorMonitor _sensors;

    public HealthCheckService(IHardwareInfoService hardware, ISensorMonitor sensors)
    {
        _hardware = hardware;
        _sensors = sensors;
    }

    public async Task<HealthReport> RunAsync(CancellationToken token = default)
    {
        var overview = await _hardware.GetOverviewAsync(token);
        var issues = new List<HealthIssue>();
        var score = 100;

        // CPU 占用
        var cpuUsage = overview.Cpu.UsagePercent;
        if (cpuUsage >= 90) { issues.Add(new HealthIssue { Category = "CPU", MessageKey = "Health_CpuHigh", MessageArg = $"{cpuUsage:F0}%", Severity = HealthSeverity.Critical }); score -= 20; }
        else if (cpuUsage >= 75) { issues.Add(new HealthIssue { Category = "CPU", MessageKey = "Health_CpuWarm", MessageArg = $"{cpuUsage:F0}%", Severity = HealthSeverity.Warning }); score -= 10; }

        // CPU 温度（来自传感器快照，可能为空）
        var cpuTemp = LatestTemperature();
        if (cpuTemp is { } t)
        {
            if (t >= 90) { issues.Add(new HealthIssue { Category = "CPU", MessageKey = "Health_TempCritical", MessageArg = $"{t:F0}°C", Severity = HealthSeverity.Critical }); score -= 25; }
            else if (t >= 80) { issues.Add(new HealthIssue { Category = "CPU", MessageKey = "Health_TempHigh", MessageArg = $"{t:F0}°C", Severity = HealthSeverity.Warning }); score -= 12; }
        }

        // 内存
        var memUsage = overview.Memory.UsagePercent;
        if (memUsage >= 90) { issues.Add(new HealthIssue { Category = "Memory", MessageKey = "Health_MemHigh", MessageArg = $"{memUsage:F0}%", Severity = HealthSeverity.Critical }); score -= 18; }
        else if (memUsage >= 80) { issues.Add(new HealthIssue { Category = "Memory", MessageKey = "Health_MemWarm", MessageArg = $"{memUsage:F0}%", Severity = HealthSeverity.Warning }); score -= 8; }

        // 磁盘健康
        var unhealthy = overview.Disks.Count(d => d.Health is not Models.Hardware.DiskHealthStatus.Healthy
                                                  && d.Health is not Models.Hardware.DiskHealthStatus.Unknown);
        if (unhealthy > 0) { issues.Add(new HealthIssue { Category = "Disk", MessageKey = "Health_DiskUnhealthy", MessageArg = unhealthy.ToString(), Severity = HealthSeverity.Critical }); score -= 20 * Math.Min(unhealthy, 2); }

        // 磁盘剩余空间（取最紧张的系统盘比例）
        var tightest = overview.Disks.SelectMany(d => d.Partitions)
            .Where(p => p.TotalBytes > 0).Select(p => p.UsedPercent).DefaultIfEmpty(0).Max();
        if (tightest >= 95) { issues.Add(new HealthIssue { Category = "Disk", MessageKey = "Health_DiskFull", MessageArg = $"{tightest:F0}%", Severity = HealthSeverity.Critical }); score -= 15; }
        else if (tightest >= 85) { issues.Add(new HealthIssue { Category = "Disk", MessageKey = "Health_DiskTight", MessageArg = $"{tightest:F0}%", Severity = HealthSeverity.Warning }); score -= 7; }

        // 电池损耗
        var wear = overview.Battery.IsFullChargeCapacityKnown ? (int)Math.Round(overview.Battery.WearPercent) : 0;
        if (overview.Battery.IsPresent && wear >= 40) { issues.Add(new HealthIssue { Category = "Battery", MessageKey = "Health_BatteryWorn", MessageArg = $"{wear}%", Severity = HealthSeverity.Warning }); score -= 8; }

        score = Math.Clamp(score, 0, 100);

        return new HealthReport
        {
            GeneratedAt = DateTime.Now,
            Score = score,
            Issues = issues,
            CpuUsagePercent = cpuUsage,
            CpuTemperature = cpuTemp,
            MemoryUsagePercent = memUsage,
            MemoryAvailableGB = overview.Memory.TotalBytes > 0 ? overview.Memory.AvailableBytes / 1024.0 / 1024 / 1024 : null,
            DiskCount = overview.Disks.Count,
            UnhealthyDiskCount = unhealthy,
            BatteryWearPercent = wear,
            IsBatteryPresent = overview.Battery.IsPresent,
            SensorCount = _sensors.LatestReadings.Count
        };
    }

    private double? LatestTemperature()
    {
        var temps = _sensors.LatestReadings
            .Where(r => r.Kind == Models.Hardware.SensorKind.Temperature
                        && r.HardwareName.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value).ToList();
        return temps.Count > 0 ? temps.Max() : null;
    }
}
