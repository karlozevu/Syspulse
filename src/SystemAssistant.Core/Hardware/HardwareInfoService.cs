using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.Windows;
using static SystemAssistant.Core.Hardware.WmiQuery;

namespace SystemAssistant.Core.Hardware;

/// <summary>
/// 整机硬件信息读取。数据来源优先级：WMI（静态信息）→ Win32 API（内存 / 显示器）
/// → LibreHardwareMonitor 传感器（实时数据，经 <see cref="ISensorMonitor"/> 注入）。
/// 所有读取都在后台线程执行且逐项容错，任何一项失败都不会影响其它项或导致崩溃。
/// </summary>
public sealed class HardwareInfoService : IHardwareInfoService
{
    private readonly ISensorMonitor? _sensors;

    public HardwareInfoService(ISensorMonitor? sensors = null) => _sensors = sensors;

    public Task<SystemOverview> GetOverviewAsync(CancellationToken token = default)
        => Task.Run(BuildOverview, token);

    public Task<CpuInfo> GetCpuAsync(CancellationToken token = default)
        => Task.Run(BuildCpu, token);

    public Task<MemoryInfo> GetMemoryAsync(CancellationToken token = default)
        => Task.Run(BuildMemory, token);

    public Task<IReadOnlyList<GpuInfo>> GetGpusAsync(CancellationToken token = default)
        => Task.Run(() => (IReadOnlyList<GpuInfo>)BuildGpus(), token);

    public Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken token = default)
        => Task.Run(() => (IReadOnlyList<DiskInfo>)BuildDisks(), token);

    public Task<BatteryInfo> GetBatteryAsync(CancellationToken token = default)
        => Task.Run(BuildBattery, token);

    public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync(CancellationToken token = default)
        => Task.Run(() => (IReadOnlyList<DisplayInfo>)WindowsNativeInfo.GetDisplays(), token);

    private SystemOverview BuildOverview() => new()
    {
        GeneratedAt = DateTime.Now,
        OperatingSystem = BuildOperatingSystem(),
        Motherboard = BuildMotherboard(),
        Bios = BuildBios(),
        Cpu = BuildCpu(),
        Memory = BuildMemory(),
        Gpus = BuildGpus(),
        Disks = BuildDisks(),
        NetworkAdapters = BuildNetworkAdapters(),
        Battery = BuildBattery(),
        Displays = WindowsNativeInfo.GetDisplays()
    };

    #region 操作系统 / 主板 / BIOS

    private static OperatingSystemInfo BuildOperatingSystem()
    {
        var os = First(CimV2, "Win32_OperatingSystem");
        var caption = os?.String("Caption") ?? RuntimeInformation.OSDescription;

        // Win32_OperatingSystem.Caption 在 Windows 11 上仍显示 "Windows 10"，
        // 因此读取注册表补充 DisplayVersion / UBR 得到更准确的展示名。
        var displayVersion = string.Empty;
        var productName = string.Empty;
        var ubr = string.Empty;
        var currentBuild = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                productName = key.GetValue("ProductName")?.ToString() ?? string.Empty;
                displayVersion = key.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                currentBuild = key.GetValue("CurrentBuild")?.ToString() ?? string.Empty;
                ubr = key.GetValue("UBR")?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        var build = os?.String("BuildNumber", "Version") ?? currentBuild;
        if (!string.IsNullOrEmpty(ubr)) build += "." + ubr;
        if (!string.IsNullOrEmpty(displayVersion)) caption += $" ({displayVersion})";
        if (!string.IsNullOrEmpty(productName) && !caption.Contains(productName, StringComparison.OrdinalIgnoreCase))
            caption += " · " + productName;

        return new OperatingSystemInfo
        {
            Caption = caption,
            Version = os?.String("Version") ?? Environment.OSVersion.Version.ToString(),
            BuildNumber = build,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            InstallDate = FormatHelper.FormatCimDateTime(os?.String("InstallDate")),
            LastBootTime = FormatHelper.FormatCimDateTime(os?.String("LastBootUpTime")),
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName
        };
    }

    private static MotherboardInfo BuildMotherboard()
    {
        var board = First(CimV2, "Win32_BaseBoard");
        return new MotherboardInfo
        {
            Manufacturer = board?.String("Manufacturer") ?? string.Empty,
            Model = board?.String("Product") ?? string.Empty,
            SerialNumber = board?.String("SerialNumber") ?? string.Empty
        };
    }

    private static BiosInfo BuildBios()
    {
        var bios = First(CimV2, "Win32_BIOS");
        return new BiosInfo
        {
            Manufacturer = bios?.String("Manufacturer") ?? string.Empty,
            Version = bios?.String("SMBIOSBIOSVersion", "Version") ?? string.Empty,
            ReleaseDate = FormatHelper.FormatCimDateTime(bios?.String("ReleaseDate")),
            SerialNumber = bios?.String("SerialNumber") ?? string.Empty
        };
    }

    private static IReadOnlyList<NetworkAdapterInfo> BuildNetworkAdapters()
    {
        var configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in Select(CimV2, "Win32_NetworkAdapterConfiguration", "IPEnabled = True"))
        {
            var ips = string.Join(", ", config.StringArray("IPAddress").Where(ip => !string.IsNullOrWhiteSpace(ip)));
            configs[config.String("SettingID")] = ips;
        }

        var result = new List<NetworkAdapterInfo>();
        foreach (var adapter in Select(CimV2, "Win32_NetworkAdapter", "NetEnabled = True"))
        {
            var guid = adapter.String("GUID");
            configs.TryGetValue(guid, out var ips);
            var speed = adapter.Int64("Speed");
            result.Add(new NetworkAdapterInfo
            {
                Name = adapter.String("NetConnectionID", "Name"),
                MacAddress = adapter.String("MACAddress"),
                IpAddresses = ips ?? string.Empty,
                Speed = speed > 0 ? FormatHelper.FormatBytes(speed) + "/s" : string.Empty,
                IsEnabled = true
            });
        }
        return result;
    }

    #endregion

    #region CPU

    private CpuInfo BuildCpu()
    {
        var processor = First(CimV2, "Win32_Processor");
        var l1 = Select(CimV2, "Win32_CacheMemory", "Level = 3").Sum(x => x.Int64("MaxCacheSize"));
        var cpu = new CpuInfo
        {
            Name = processor?.String("Name") ?? string.Empty,
            Manufacturer = processor?.String("Manufacturer") ?? string.Empty,
            Socket = processor?.String("SocketDesignation") ?? string.Empty,
            PhysicalCores = processor?.Int32("NumberOfCores", "NumberOfEnabledCore") ?? 0,
            LogicalProcessors = processor?.Int32("NumberOfLogicalProcessors") ?? Environment.ProcessorCount,
            BaseClockMHz = processor?.Double("MaxClockSpeed") ?? 0,
            // WMI 不提供睿频规格；保持 0，界面显示 N/A（详见 README「已知限制」）
            MaxTurboMHz = 0,
            L1CacheKB = l1,
            L2CacheKB = processor?.Int64("L2CacheSize") ?? 0,
            L3CacheKB = processor?.Int64("L3CacheSize") ?? 0,
            UsagePercent = WindowsNativeInfo.GetCpuUsagePercent() ?? processor?.Double("LoadPercentage") ?? 0,
            CurrentClockMHz = processor?.Double("CurrentClockSpeed") ?? 0
        };

        // 用传感器数据覆盖 / 补充实时指标
        var cpuReadings = _sensors?.LatestReadings
            .Where(r => r.HardwareName.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                        || r.Identifier.Contains("cpu", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<SensorReading>();

        var temperatures = cpuReadings.Where(r => r.Kind == SensorKind.Temperature).Select(r => r.Value).ToList();
        if (temperatures.Count > 0) cpu.TemperatureCelsius = temperatures.Max();

        var clocks = cpuReadings.Where(r => r.Kind == SensorKind.Clock && r.Value > 0).Select(r => r.Value).ToList();
        if (clocks.Count > 0) cpu.CurrentClockMHz = clocks.Max();

        var power = cpuReadings.Where(r => r.Kind == SensorKind.Power).Select(r => r.Value).FirstOrDefault();
        if (power > 0) cpu.PackagePowerWatt = power;

        var totalLoad = cpuReadings.FirstOrDefault(r => r.Kind == SensorKind.Load
            && r.Name.Contains("Total", StringComparison.OrdinalIgnoreCase));
        if (totalLoad is not null) cpu.UsagePercent = totalLoad.Value;

        return cpu;
    }

    #endregion

    #region 内存

    private static MemoryInfo BuildMemory()
    {
        var (total, available, commitTotal, commitAvailable) = WindowsNativeInfo.GetMemoryStatus();
        var osMemory = First(CimV2, "Win32_OperatingSystem");
        if (total <= 0) total = (osMemory?.Int64("TotalVisibleMemorySize") ?? 0) * 1024;
        if (total <= 0) total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) available = (osMemory?.Int64("FreePhysicalMemory") ?? 0) * 1024;
        available = Math.Clamp(available, 0, total);

        var modules = new List<MemoryModuleInfo>();
        foreach (var module in Select(CimV2, "Win32_PhysicalMemory"))
        {
            modules.Add(new MemoryModuleInfo
            {
                Manufacturer = module.String("Manufacturer"),
                PartNumber = module.String("PartNumber"),
                CapacityBytes = module.Int64("Capacity"),
                SpeedMHz = module.Int64("Speed", "ConfiguredClockSpeed"),
                Slot = module.String("DeviceLocator", "BankLabel")
            });
        }

        // 页面文件：GlobalMemoryStatusEx 的提交上限减去当前可用提交量即已用分页
        var pageUsed = Math.Max(0, commitTotal - commitAvailable);
        var pageFile = First(CimV2, "Win32_PageFileUsage");
        var pageTotal = commitTotal > 0 ? commitTotal : (pageFile?.Int64("AllocatedBaseSize") ?? 0) * 1024 * 1024;

        var slotCount = First(CimV2, "Win32_PhysicalMemoryArray")?.Int32("MemoryDevices") ?? 0;
        var channel = modules.Count == 0
            ? "未检测到物理内存条信息"
            : $"{modules.Count} 条内存 / {slotCount} 个插槽（WMI 不提供通道数，此处显示插槽占用）";

        return new MemoryInfo
        {
            TotalBytes = total,
            UsedBytes = total - available,
            AvailableBytes = available,
            PageFileUsedBytes = pageUsed,
            PageFileTotalBytes = pageTotal,
            ModuleCount = modules.Count,
            ChannelDescription = channel,
            Modules = modules
        };
    }

    #endregion

    #region 显卡

    private IReadOnlyList<GpuInfo> BuildGpus()
    {
        var gpus = new List<GpuInfo>();
        foreach (var controller in Select(CimV2, "Win32_VideoController"))
        {
            var gpu = new GpuInfo
            {
                Name = controller.String("Name"),
                DriverVersion = controller.String("DriverVersion"),
                DriverDate = FormatHelper.FormatCimDateTime(controller.String("DriverDate")),
                VideoProcessor = controller.String("VideoProcessor"),
                Resolution = $"{controller.Int64("CurrentHorizontalResolution")} x {controller.Int64("CurrentVerticalResolution")}",
                // Win32_VideoController.AdapterRAM 为 32 位字段，超过 4 GB 时会被截断，仅作参考
                VramTotalBytes = controller.Int64("AdapterRAM")
            };
            gpus.Add(gpu);
        }

        // 传感器实时数据按显卡名称模糊匹配
        var readings = _sensors?.LatestReadings
            .Where(r => r.HardwareName.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                        || r.Identifier.Contains("gpu", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<SensorReading>();

        foreach (var gpu in gpus)
        {
            var matched = readings.Where(r =>
            {
                var shortName = gpu.Name.Split('(')[0].Trim();
                var keyword = shortName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? shortName;
                return r.HardwareName.Contains(keyword, StringComparison.OrdinalIgnoreCase) || readings.Count <= 8;
            }).ToList();

            var temperature = matched.Where(r => r.Kind == SensorKind.Temperature).Select(r => r.Value).ToList();
            if (temperature.Count > 0) gpu.TemperatureCelsius = temperature.Max();

            var load = matched.FirstOrDefault(r => r.Kind == SensorKind.Load
                && r.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
            if (load is not null) gpu.LoadPercent = load.Value;

            var memory = matched.FirstOrDefault(r =>
                r.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) && r.Unit.Contains("MB", StringComparison.OrdinalIgnoreCase));
            if (memory is not null) gpu.VramUsedBytes = (long)(memory.Value * 1024 * 1024);
        }
        return gpus;
    }

    #endregion

    #region 磁盘 / SMART

    private static IReadOnlyList<DiskInfo> BuildDisks()
    {
        // MSFT_PhysicalDisk 提供可靠的 HealthStatus 与 MediaType（Windows 8+ 内置，无需驱动）
        var storageByIndex = new Dictionary<int, WmiObject>();
        foreach (var physical in Select(Storage, "MSFT_PhysicalDisk"))
        {
            var index = physical.Int32("DeviceId");
            storageByIndex[index] = physical;
        }

        var smartRaw = Select(WmiRoot, "MSStorageDriver_ATAPISmartData");
        var disks = new List<DiskInfo>();
        ForEach(CimV2, "Win32_DiskDrive", drive =>
        {
            var index = Convert.ToInt32(drive["Index"] ?? 0, CultureInfo.InvariantCulture);
            storageByIndex.TryGetValue(index, out var physical);

            var mediaType = DiskMediaType.Unknown;
            var health = DiskHealthStatus.Unknown;
            if (physical is not null)
            {
                mediaType = physical.Int32("MediaType") switch
                {
                    4 => DiskMediaType.SolidStateDrive,
                    3 => DiskMediaType.HardDiskDrive,
                    _ => GuessMediaType(drive["Model"]?.ToString(), drive["MediaType"]?.ToString())
                };
                health = physical.Int32("HealthStatus") switch
                {
                    2 => DiskHealthStatus.Failed,
                    1 => DiskHealthStatus.Warning,
                    0 => DiskHealthStatus.Healthy,
                    _ => DiskHealthStatus.Unknown
                };
            }
            else
            {
                mediaType = GuessMediaType(drive["Model"]?.ToString(), drive["MediaType"]?.ToString());
            }

            var smart = BuildSmartInfo(index, health, smartRaw, drive["Status"]?.ToString());
            var partitions = new List<PartitionInfo>();
            try
            {
                foreach (ManagementObject partition in drive.GetRelated("Win32_DiskPartition"))
                {
                    using (partition)
                    {
                        foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                        {
                            using (logical)
                            {
                                // DriveType 3 = 本地固定磁盘
                                if (Convert.ToInt32(logical["DriveType"] ?? 0, CultureInfo.InvariantCulture) != 3) continue;
                                var total = Convert.ToInt64(logical["Size"] ?? 0, CultureInfo.InvariantCulture);
                                var free = Convert.ToInt64(logical["FreeSpace"] ?? 0, CultureInfo.InvariantCulture);
                                partitions.Add(new PartitionInfo
                                {
                                    DriveLetter = logical["DeviceID"]?.ToString() ?? string.Empty,
                                    VolumeLabel = logical["VolumeName"]?.ToString() ?? string.Empty,
                                    FileSystem = logical["FileSystem"]?.ToString() ?? string.Empty,
                                    TotalBytes = total,
                                    FreeBytes = free
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

            disks.Add(new DiskInfo
            {
                DiskIndex = index,
                Model = drive["Model"]?.ToString()?.Trim() ?? string.Empty,
                SerialNumber = drive["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
                InterfaceType = drive["InterfaceType"]?.ToString() ?? string.Empty,
                TotalBytes = Convert.ToInt64(drive["Size"] ?? 0, CultureInfo.InvariantCulture),
                MediaType = mediaType,
                Health = health,
                Smart = smart,
                Partitions = partitions
            });
        });
        return disks;
    }

    private static DiskMediaType GuessMediaType(string? model, string? media)
    {
        var text = $"{model} {media}";
        if (text.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Solid State", StringComparison.OrdinalIgnoreCase)) return DiskMediaType.SolidStateDrive;
        if (text.Contains("Removable", StringComparison.OrdinalIgnoreCase)) return DiskMediaType.Removable;
        if (!string.IsNullOrWhiteSpace(model)) return DiskMediaType.HardDiskDrive;
        return DiskMediaType.Unknown;
    }

    /// <summary>
    /// SMART 信息为「尽力而为」：Windows 没有统一的公开 SMART 接口，
    /// 这里结合 MSFT_PhysicalDisk.HealthStatus、Win32_DiskDrive.Status 与
    /// MSStorageDriver_ATAPISmartData 的原始属性块，任何一步失败都不影响磁盘列表。
    /// </summary>
    private static SmartInfo BuildSmartInfo(int index, DiskHealthStatus health,
        IReadOnlyList<WmiObject> smartRaw, string? wmiStatus)
    {
        var attributes = new List<SmartAttributeItem>();
        double? temperature = null;
        int? powerOnHours = null;
        int? cycles = null;

        try
        {
            // 原始 SMART 数据通常按物理磁盘顺序返回；按顺序取对应索引，取不到就跳过属性明细
            if (index < smartRaw.Count)
            {
                var data = smartRaw[index].Bytes("VendorSpecific");
                if (data is { Length: >= 362 })
                {
                    for (var slot = 0; slot < 30; slot++)
                    {
                        var offset = 2 + slot * 12;
                        if (offset + 11 >= data.Length) break;
                        var id = data[offset];
                        if (id == 0) continue;
                        long raw = data[offset + 5]
                                   | ((long)data[offset + 6] << 8)
                                   | ((long)data[offset + 7] << 16)
                                   | ((long)data[offset + 8] << 24);
                        var name = SmartAttributeNames.TryGetValue(id, out var n) ? n : $"属性 0x{id:X2}";
                        var critical = id is 5 or 197 or 198 or 200 or 202;
                        attributes.Add(new SmartAttributeItem
                        {
                            Id = id,
                            Name = name,
                            CurrentValue = data[offset + 3],
                            WorstValue = data[offset + 4],
                            Threshold = data[offset + 11],
                            RawValue = raw,
                            IsWarning = critical && raw > 0
                        });
                        if (id == 194) temperature = raw & 0xFF;
                        if (id == 9) powerOnHours = (int)Math.Min(raw, int.MaxValue);
                        if (id == 12) cycles = (int)Math.Min(raw, int.MaxValue);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { }

        var predictedFailure = First(WmiRoot, "MSStorageDriver_FailurePredictStatus")?.Bool("PredictFailure") ?? false;
        var hasCritical = attributes.Any(a => a.IsWarning);
        var effectiveHealth = health == DiskHealthStatus.Unknown
            ? (predictedFailure || hasCritical ? DiskHealthStatus.Warning : DiskHealthStatus.Unknown)
            : health;
        if (effectiveHealth == DiskHealthStatus.Healthy && (predictedFailure || hasCritical))
            effectiveHealth = DiskHealthStatus.Warning;
        if (wmiStatus?.Contains("Pred Fail", StringComparison.OrdinalIgnoreCase) == true)
            effectiveHealth = DiskHealthStatus.Warning;
        if (wmiStatus?.Contains("Error", StringComparison.OrdinalIgnoreCase) == true)
            effectiveHealth = DiskHealthStatus.Failed;

        var summary = effectiveHealth switch
        {
            DiskHealthStatus.Healthy => "健康",
            DiskHealthStatus.Warning => "警告：SMART 报告存在风险属性，建议尽快备份数据",
            DiskHealthStatus.Failed => "故障：请立即备份数据并检测硬盘",
            _ => "未知：系统未提供 SMART 健康状态"
        };

        return new SmartInfo
        {
            IsAvailable = health != DiskHealthStatus.Unknown || attributes.Count > 0,
            Health = effectiveHealth,
            Summary = summary,
            TemperatureCelsius = temperature,
            PowerOnHours = powerOnHours,
            PowerCycleCount = cycles,
            Attributes = attributes
        };
    }

    private static readonly IReadOnlyDictionary<byte, string> SmartAttributeNames = new Dictionary<byte, string>
    {
        [1] = "读取错误率", [3] = "寻道时间", [5] = "重映射扇区数", [9] = "通电时间",
        [12] = "通电次数", [177] = "磨损均衡计数", [190] = "气流温度", [194] = "温度",
        [197] = "待映射扇区数", [198] = "无法校正扇区数", [199] = "CRC 错误计数",
        [200] = "写入错误率", [202] = "剩余寿命", [233] = "介质磨损指标", [241] = "总写入量"
    };

    #endregion

    #region 电池

    private static BatteryInfo BuildBattery()
    {
        var win32Battery = First(CimV2, "Win32_Battery");
        var isPresent = win32Battery is not null || Select(CimV2, "Win32_Battery").Count > 0;
        if (!isPresent) return new BatteryInfo { IsPresent = false };

        // root\WMI 提供更精确的设计容量、满充容量与循环次数
        var staticData = First(WmiRoot, "BatteryStaticData");
        var fullCharged = First(WmiRoot, "BatteryFullChargedCapacity");
        var status = First(WmiRoot, "BatteryStatus");

        var design = staticData?.Int32("DesignedCapacity") ?? 0;
        var full = fullCharged?.Int32("FullChargedCapacity") ?? 0;
        var remaining = status?.Int32("RemainingCapacity") ?? 0;
        var cycleCount = staticData?.Int32("CycleCount") ?? -1;

        // 部分设备只报告百分比，用设计容量折算，避免损耗率显示为 100%
        if (full == 0 && design > 0)
        {
            var percent = win32Battery?.Int32("EstimatedChargeRemaining") ?? 0;
            full = design;
            remaining = (int)(design * percent / 100.0);
        }

        return new BatteryInfo
        {
            IsPresent = true,
            DesignCapacityMwh = design,
            FullChargeCapacityMwh = full,
            RemainingCapacityMwh = remaining,
            CycleCount = cycleCount > 0 ? cycleCount : -1,
            IsCharging = status?.Bool("Charging") ?? (win32Battery?.Int32("BatteryStatus") == 2),
            Chemistry = staticData?.String("Chemistry") ?? string.Empty
        };
    }

    #endregion
}
