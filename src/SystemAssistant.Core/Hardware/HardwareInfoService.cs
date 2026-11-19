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
        // WmiObject.String() 恒返回非 null（缺失时是空串），因此必须显式判空再回落
        var caption = os?.String("Caption");
        if (string.IsNullOrWhiteSpace(caption)) caption = RuntimeInformation.OSDescription;

        // 注册表的 DisplayVersion（如 24H2）与 UBR 可补全展示信息。
        // 注意：ProductName 是历史遗留值，Windows 11 上仍写 "Windows 10 Pro"，
        // 早期版本把它拼到 Caption 后面，导致「系统」一行同时出现两个互相矛盾的系统名，
        // 因此这里刻意不再读取和使用 ProductName。
        var displayVersion = string.Empty;
        var currentBuild = string.Empty;
        var ubr = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                displayVersion = key.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                currentBuild = key.GetValue("CurrentBuild")?.ToString() ?? string.Empty;
                ubr = key.GetValue("UBR")?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        var buildNumber = os?.String("BuildNumber", "Version");
        if (string.IsNullOrWhiteSpace(buildNumber)) buildNumber = currentBuild;
        caption = NormalizeWindowsCaption(caption, buildNumber);
        if (!string.IsNullOrEmpty(displayVersion)
            && !caption.Contains(displayVersion, StringComparison.OrdinalIgnoreCase))
            caption += $" ({displayVersion})";

        var build = buildNumber;
        if (!string.IsNullOrEmpty(ubr)) build += "." + ubr;

        var version = os?.String("Version");
        if (string.IsNullOrWhiteSpace(version)) version = Environment.OSVersion.Version.ToString();

        return new OperatingSystemInfo
        {
            Caption = caption,
            Version = version,
            BuildNumber = build,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            InstallDate = FormatHelper.FormatCimDateTime(os?.String("InstallDate")),
            LastBootTime = FormatHelper.FormatCimDateTime(os?.String("LastBootUpTime")),
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName
        };
    }

    /// <summary>
    /// Win32_OperatingSystem.Caption 在 Windows 11 上仍返回 "Microsoft Windows 10 ..."。
    /// 用 BuildNumber（Windows 11 起为 22000）修正这处历史遗留错误，只替换字面
    /// "Windows 10"，保留厂商前缀、SKU 与语言（如 "Microsoft ... 专业版"）。
    /// Windows Server 的 Caption 不含该字面，因此不会被误改。
    /// </summary>
    private static string NormalizeWindowsCaption(string caption, string buildNumber)
    {
        const string legacy = "Windows 10";
        if (!int.TryParse(buildNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build)
            || build < 22000) return caption;
        var index = caption.IndexOf(legacy, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return caption;
        return caption[..index] + "Windows 11" + caption[(index + legacy.Length)..];
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
                // Win32_NetworkAdapter.Speed 的单位是 bit/s，必须先除以 8 才是字节/秒，
                // 否则千兆网卡会显示成 953 MB/s（实际约 119 MB/s）。
                Speed = speed > 0 ? FormatHelper.FormatSpeed(speed / 8.0) : string.Empty,
                IsEnabled = true
            });
        }
        return result;
    }

    #endregion

    #region CPU

    /// <summary>
    /// CPU 三级缓存容量（KB）。依据 WMI / CIM 语义：
    /// Win32_CacheMemory.Level：3=Primary(L1)、4=Secondary(L2)、5=Tertiary(L3)。
    /// 注意 Level 的数字是「层级代号」，并不等于 L1/L2/L3 的序号，切勿把 Level=3 误当成 L3。
    /// Win32_CacheMemory.MaxCacheSize 与 Win32_Processor.L2CacheSize / L3CacheSize 单位均为 KB。
    /// L1 取 Level=3(Primary) 的合计；L2 / L3 优先用 Win32_Processor 的规格值，
    /// 取不到时回落到 Level=4(Secondary) / Level=5(Tertiary) 的合计；任何一级不可靠都置 0（界面显示 N/A）。
    /// </summary>
    private static (long L1, long L2, long L3) BuildCacheSizes(WmiObject? processor)
    {
        var caches = Select(CimV2, "Win32_CacheMemory");
        long SumByLevel(int level) => Math.Max(0, caches
            .Where(c => c.Int32("Level") == level)
            .Sum(c => c.Int64("MaxCacheSize")));

        var l1 = SumByLevel(3);                                    // Primary = L1
        var l2 = Math.Max(0, processor?.Int64("L2CacheSize") ?? 0);
        var l3 = Math.Max(0, processor?.Int64("L3CacheSize") ?? 0);
        if (l2 == 0) l2 = SumByLevel(4);                           // Secondary = L2
        if (l3 == 0) l3 = SumByLevel(5);                           // Tertiary = L3
        return (l1, l2, l3);
    }

    private CpuInfo BuildCpu()
    {
        var processor = First(CimV2, "Win32_Processor");
        var (l1Cache, l2Cache, l3Cache) = BuildCacheSizes(processor);
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
            L1CacheKB = l1Cache,
            L2CacheKB = l2Cache,
            L3CacheKB = l3Cache,
            UsagePercent = WindowsNativeInfo.GetCpuUsagePercent("hardware-detail") ?? processor?.Double("LoadPercentage") ?? 0,
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

        // 页面文件：GlobalMemoryStatusEx 的 ullTotalPageFile 是"提交上限"（物理内存+页面配额），
        // 并非页面文件本身大小。优先用 Win32_PageFileUsage 求真实页面文件大小；
        // 读不到时才回退到提交上限（并让已用量也用提交值，保持分子分母同源）。
        long pageTotal = 0;
        try
        {
            foreach (var pf in Select(CimV2, "Win32_PageFileUsage"))
                pageTotal += pf.Int64("AllocatedBaseSize") * 1024L * 1024L;
        }
        catch (Exception) { pageTotal = 0; }
        long pageUsed;
        if (pageTotal > 0)
        {
            // 已用页面文件 = 提交已用 - 物理内存已用，下限 0
            pageUsed = Math.Clamp((commitTotal - commitAvailable) - (total - available), 0, pageTotal);
        }
        else
        {
            // 无页面文件 WMI 数据时，退化为提交语义（总量=提交上限、已用=提交已用）
            pageTotal = Math.Max(0, commitTotal);
            pageUsed = Math.Max(0, commitTotal - commitAvailable);
        }

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
        // 显存优先取驱动注册表的 64 位 QWORD。Win32_VideoController.AdapterRAM 是
        // uint32，8 GB / 12 GB 显卡会回绕成 2 GB 或饱和到 4 GB，是长期误报的根源。
        var registryVram = ReadGpuVramFromRegistry();
        var matchedAdapters = new HashSet<int>();
        var gpus = new List<GpuInfo>();
        foreach (var controller in Select(CimV2, "Win32_VideoController"))
        {
            var name = controller.String("Name");
            var (vram, source) = ResolveVram(name, controller.Int64("AdapterRAM"), registryVram, matchedAdapters);
            var gpu = new GpuInfo
            {
                Name = name,
                DriverVersion = controller.String("DriverVersion"),
                DriverDate = FormatHelper.FormatCimDateTime(controller.String("DriverDate")),
                VideoProcessor = controller.String("VideoProcessor"),
                Resolution = $"{controller.Int64("CurrentHorizontalResolution")} x {controller.Int64("CurrentVerticalResolution")}",
                VramTotalBytes = vram,
                VramSource = source
            };
            gpus.Add(gpu);
        }

        // 传感器实时数据按 GPU 硬件序号匹配（Identifier 形如 /gpu-nvidia/0/temperature/3）。
        // 不能按型号关键词匹配：LHM 的 HardwareName 只有 "GPU GpuNvidia" 之类，不含
        // "RTX 3080" 这样的型号，旧实现 Contains(keyword) 恒为 false、实时读数全部丢失。
        var readings = _sensors?.LatestReadings;
        var gpuReadings = readings is null
            ? Array.Empty<SensorReading>()
            : readings.Where(r => r.Identifier.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase)
                                  || r.HardwareName.Contains("GPU", StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var gpu in gpus)
        {
            // 用「厂商关键词」建立稳定关联，而不是 WMI 列表位置 i：
            // WMI Win32_VideoController 的返回顺序与 LHM 的 GPU 序号体系无任何保证，
            // 双显卡机器上按位置关联会把 A 卡的温度/负载写到 B 卡上。
            var vendorKey = GpuVendorKey(gpu.Name);
            var matched = gpuReadings.Where(r => GpuReadingBelongsTo(r.Identifier, vendorKey)).ToList();
            if (matched.Count == 0) continue;

            var temperature = matched.Where(r => r.Kind == SensorKind.Temperature).Select(r => r.Value).ToList();
            if (temperature.Count > 0) gpu.TemperatureCelsius = temperature.Max();

            var load = matched.FirstOrDefault(r => r.Kind == SensorKind.Load
                && r.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
            if (load is not null) gpu.LoadPercent = load.Value;

            // "GPU Memory Used" 在 LHM 中是 SmallData 类型（数值单位固定为 MB），
            // MapKind 归为 Other、MapUnit 返回空串，因此不能依赖 Unit.Contains("MB")。
            var memory = matched.FirstOrDefault(r =>
                r.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase));
            if (memory is not null) gpu.VramUsedBytes = (long)(memory.Value * 1024 * 1024);
        }
        return gpus;
    }

    /// <summary>
    /// 从 WMI 显卡名提取稳定的厂商关键词：LHM 的 GPU Identifier 首段是
    /// gpu-nvidia / gpu-amd / gpu-intel，与 WMI 名称里的 NVIDIA / AMD / Radeon / Intel 对应。
    /// 返回 null 表示无法判定厂商（此时只能保守不关联，避免错配到别的卡）。
    /// </summary>
    private static string? GpuVendorKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)) return "nvidia";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ATI", StringComparison.OrdinalIgnoreCase)) return "amd";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "intel";
        return null;
    }

    /// <summary>
    /// 判断一条 LHM 传感器读数是否属于指定厂商的显卡。
    /// Identifier 形如 /gpu-nvidia/0/temperature/3，首段硬件类型含稳定厂商名。
    /// </summary>
    private static bool GpuReadingBelongsTo(string identifier, string? vendorKey)
    {
        if (string.IsNullOrEmpty(identifier) || vendorKey is null) return false;
        var parts = identifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !parts[0].StartsWith("gpu", StringComparison.OrdinalIgnoreCase)) return false;
        return parts[0].Contains(vendorKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从显卡驱动注册表读取 64 位显存容量。
    /// HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}\000N 下的
    /// HardwareInformation.qwMemorySize 是 REG_QWORD，能正确表示 8 GB 以上显存；
    /// 旧驱动的 HardwareInformation.MemorySize 是 REG_DWORD，作为次级来源。
    /// 普通用户权限即可读取，不需要管理员。
    /// </summary>
    private static List<(string Description, long Bytes)> ReadGpuVramFromRegistry()
    {
        var result = new List<(string, long)>();
        const string path = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root is null) return result;
            foreach (var subName in root.GetSubKeyNames())
            {
                if (subName.Length == 0 || !subName.All(char.IsDigit)) continue;
                using var key = root.OpenSubKey(subName);
                if (key is null) continue;
                var description = key.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(description)) continue;

                var bytes = ReadRegistryLong(key.GetValue("HardwareInformation.qwMemorySize"));
                if (bytes <= 0) bytes = ReadRegistryLong(key.GetValue("HardwareInformation.MemorySize"));
                // 只接受 32 MB ~ 128 GB 的合理值，过滤 0xffffffff 之类的哨兵数据
                if (bytes >= 32L * 1024 * 1024 && bytes <= 128L * 1024 * 1024 * 1024)
                    result.Add((description, bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return result;
    }

    private static long ReadRegistryLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        uint u => u,
        byte[] raw when raw.Length >= 8 => BitConverter.ToInt64(raw, 0),
        byte[] raw when raw.Length == 4 => BitConverter.ToInt32(raw, 0),
        _ => 0
    };

    private static (long Bytes, string Source) ResolveVram(
        string name, long wmiBytes,
        IReadOnlyList<(string Description, long Bytes)> registry,
        HashSet<int> matchedAdapters)
    {
        var candidates = new List<(int Score, int Index, long Bytes)>();
        for (var i = 0; i < registry.Count; i++)
        {
            if (matchedAdapters.Contains(i)) continue;
            var score = MatchGpuName(name, registry[i].Description);
            if (score > 0) candidates.Add((score, i, registry[i].Bytes));
        }
        if (candidates.Count > 0)
        {
            var best = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Index).First();
            matchedAdapters.Add(best.Index);
            return (best.Bytes, "Registry");   // 64 位精确值
        }

        // WMI AdapterRAM 是 uint32：>= 4 GB 会回绕或饱和，接近上限时不可信，直接标为未知。
        const long wmiLimit = 4L * 1024 * 1024 * 1024;
        if (wmiBytes > 0 && wmiBytes < wmiLimit - 64L * 1024 * 1024)
            return (wmiBytes, "WMI");
        return (0, "Unknown");
    }

    private static int MatchGpuName(string wmiName, string description)
    {
        if (string.IsNullOrWhiteSpace(wmiName) || string.IsNullOrWhiteSpace(description)) return 0;
        if (string.Equals(wmiName, description, StringComparison.OrdinalIgnoreCase)) return 100;
        if (wmiName.Contains(description, StringComparison.OrdinalIgnoreCase)
            || description.Contains(wmiName, StringComparison.OrdinalIgnoreCase)) return 80;
        // 至少两个长度 >= 3 的共同词才算匹配，避免把虚拟显示器误配到实体显卡
        var left = wmiName.Split(new[] { ' ', '(', ')', '[', ']', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).Select(t => t.ToLowerInvariant()).ToHashSet();
        var right = description.Split(new[] { ' ', '(', ')', '[', ']', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).Select(t => t.ToLowerInvariant()).ToHashSet();
        var shared = left.Intersect(right).Count();
        return shared >= 2 ? 40 + shared : 0;
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
                        var current = data[offset + 3];
                        var worst = data[offset + 4];
                        var threshold = data[offset + 11];
                        // 这些「不良事件计数」类属性只要原始值 > 0 即代表已出现异常
                        var rawCritical = id is 5 or 197 or 198 or 200;
                        // 202（剩余寿命）、233（介质磨损）、177（磨损均衡）是寿命 / 归一化值属性，
                        // 其 raw 通常是「剩余百分比」等恒大于 0 的值，绝不能用 raw > 0 判告警（否则 SSD 恒告警）。
                        // 这类属性按归一化 Current/Worst 是否触及厂商 Threshold 判断。
                        var lifeAttribute = id is 202 or 233 or 177;
                        bool isWarning;
                        if (rawCritical)
                            isWarning = raw > 0;
                        else if (lifeAttribute)
                            isWarning = threshold > 0 && (current <= threshold || worst <= threshold);
                        else
                            isWarning = false;
                        attributes.Add(new SmartAttributeItem
                        {
                            Id = id,
                            Name = name,
                            CurrentValue = current,
                            WorstValue = worst,
                            Threshold = threshold,
                            RawValue = raw,
                            IsWarning = isWarning
                        });
                        if (id == 194)
                        {
                            // 多数 ATA 盘把温度放在 raw 最低字节；部分厂商 / NVMe 转接
                            // 编码在高位字节，此时低字节为 0。低字节为 0 时尝试各字节，
                            // 取第一个落在合理温度区间的值；都取不到则保持 null 显示 N/A。
                            var low = raw & 0xFF;
                            if (low >= 1 && low <= 120) temperature = low;
                            else
                            {
                                for (var shift = 8; shift < 64 && low == 0; shift += 8)
                                {
                                    var b = (raw >> shift) & 0xFF;
                                    if (b >= 1 && b <= 120) { temperature = b; break; }
                                }
                            }
                        }
                        if (id == 9) powerOnHours = (int)Math.Min(raw, int.MaxValue);
                        if (id == 12) cycles = (int)Math.Min(raw, int.MaxValue);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { }

        // FailurePredictStatus 可能返回多条实例（多物理磁盘）。旧实现固定取首条，
        // 导致一块盘的故障预测污染所有磁盘。按磁盘索引取对应实例，取不到则视为未报告，
        // 而不是套用其它盘的结果。
        var predictStatuses = Select(WmiRoot, "MSStorageDriver_FailurePredictStatus").ToArray();
        var predictedFailure = index < predictStatuses.Length
            && predictStatuses[index].Bool("PredictFailure");
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

        // 满充容量缺失时绝不能拿设计容量冒充：那会让损耗率恒显示为 0%（完全失真）。
        // 保留 full = 0 并标记容量信息未知，由界面显示「未知」而不是伪造一个健康的损耗率。
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
