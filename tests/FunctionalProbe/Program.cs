// System Assistant 只读功能探针（可复跑的回归测试，不修改系统）
using System.Text;
using System.Text.Json;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Core.Common;
using SystemAssistant.Core.Hardware;
using SystemAssistant.Core.Processes;
using SystemAssistant.Models.Cleaning;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Models.Toolbox;
using SystemAssistant.Utils.IO;
using SystemAssistant.Utils.Windows;

// 只读功能探针：逐个调用 Core/Utils 的公开能力，记录真实返回值与异常。
// 严格不执行任何删除、注册表写入、服务启停、系统命令等写操作。
// 从输出目录反推仓库根目录：<root>/tests/FunctionalProbe/bin/Release/net9.0-windows 上溯 5 级
var Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", ".."));
var report = new StringBuilder();
var results = new List<(string Name, bool Ok, string Detail)>();

void Emit(string text)
{
    Console.WriteLine(text);
    report.AppendLine(text);
}

async Task Test(string name, Func<Task<string>> work)
{
    try
    {
        var detail = await work().WaitAsync(TimeSpan.FromSeconds(90));
        results.Add((name, true, detail));
        Emit($"[PASS] {name}: {detail}");
    }
    catch (Exception ex)
    {
        results.Add((name, false, ex.ToString()));
        Emit($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
    }
}

Task TestSync(string name, Func<string> work) => Test(name, () => Task.FromResult(work()));

var hub = ServiceHub.Instance;
var temp = Path.Combine(Path.GetTempPath(), "sa-probe-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(temp);
Emit($"# System Assistant 功能探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Emit($"管理员权限: {PrivilegeHelper.IsAdministrator}");
Emit($"临时目录: {temp}");
Emit("");

Emit("## 一、配置与日志");
await TestSync("Settings.Current", () =>
    $"theme={hub.Settings.Current.Theme}, lang={hub.Settings.Current.Language}, interval={hub.Settings.Current.RefreshIntervalMs}ms, whitelist={hub.Settings.Current.Whitelist.Count}, customPaths={hub.Settings.Current.CustomScanPaths.Count}");
await TestSync("OperationLog.GetAll", () => $"{hub.OperationLog.GetAll().Count} 条");

Emit("");
Emit("## 二、硬件信息与报告");
SystemOverview? overview = null;
await Test("Hardware.GetOverviewAsync", async () =>
{
    overview = await hub.Hardware.GetOverviewAsync();
    return $"OS={overview.OperatingSystem.Caption[..Math.Min(28, overview.OperatingSystem.Caption.Length)]}, CPU={overview.Cpu.Name[..Math.Min(24, overview.Cpu.Name.Length)]}, GPU={overview.Gpus.Count}, Disk={overview.Disks.Count}, NIC={overview.NetworkAdapters.Count}, Battery={overview.Battery.IsPresent}, Display={overview.Displays.Count}";
});
await Test("Hardware.GetCpuAsync", async () =>
{
    var c = await hub.Hardware.GetCpuAsync();
    return $"{c.Name}, cores={c.PhysicalCores}/{c.LogicalProcessors}, base={c.BaseClockMHz}MHz, turbo={c.MaxTurboMHz}MHz, L1={c.L1CacheKB}KB L2={c.L2CacheKB}KB L3={c.L3CacheKB}KB, temp={c.TemperatureCelsius}, power={c.PackagePowerWatt}";
});
await Test("Hardware.GetMemoryAsync", async () =>
{
    var m = await hub.Hardware.GetMemoryAsync();
    return $"total={m.TotalBytes}, used={m.UsedBytes}, page={m.PageFileTotalBytes}, channel={m.ChannelDescription}, modules={m.ModuleCount}, modules={m.Modules.Count}";
});
await Test("Hardware.GetGpusAsync", async () =>
{
    var g = await hub.Hardware.GetGpusAsync();
    return $"count={g.Count}, " + string.Join("; ", g.Select(x => $"{x.Name[..Math.Min(20, x.Name.Length)]} vram={x.VramTotalBytes} used={x.VramUsedBytes} load={x.LoadPercent} temp={x.TemperatureCelsius}"));
});
await Test("Hardware.GetDisksAsync", async () =>
{
    var d = await hub.Hardware.GetDisksAsync();
    return $"count={d.Count}, " + string.Join("; ", d.Select(x => $"#{x.DiskIndex} {x.Model[..Math.Min(16, x.Model.Length)]} health={x.Health} smart={x.Smart.IsAvailable} parts={x.Partitions.Count}"));
});
await Test("Hardware.GetBatteryAsync", async () =>
{
    var b = await hub.Hardware.GetBatteryAsync();
    return $"present={b.IsPresent}, known={b.IsFullChargeCapacityKnown}, design={b.DesignCapacityMwh}, full={b.FullChargeCapacityMwh}, wear={b.WearPercent:F1}%, cycles={b.CycleCount}";
});
await Test("Hardware.GetDisplaysAsync", async () =>
{
    var d = await hub.Hardware.GetDisplaysAsync();
    return $"count={d.Count}, " + string.Join("; ", d.Select(x => $"{x.DeviceName} {x.ResolutionText} hdr={x.HdrEnabled}"));
});
if (overview is not null)
{
    await TestSync("Reports.BuildPlainText", () => $"{hub.Reports.BuildPlainText(overview!).Length} chars");
    await TestSync("Reports.BuildMarkdown", () => $"{hub.Reports.BuildMarkdown(overview!).Length} chars");
    await TestSync("Reports.BuildHtml", () => $"{hub.Reports.BuildHtml(overview!).Length} chars");
    foreach (var fmt in new[] { "txt", "md", "html" })
        await TestSync($"Reports.Export({fmt})", () =>
        {
            var p = hub.Reports.Export(overview!, temp, fmt);
            return $"{Path.GetFileName(p)} {new FileInfo(p).Length} bytes";
        });
}

Emit("");
Emit("## 三、传感器与进程采集");
await Test("Sensors.Start + 3s", async () =>
{
    var count = 0;
    var snapReadings = -1;
    hub.Sensors.SnapshotUpdated += s => { Interlocked.Increment(ref count); snapReadings = s.Readings.Count; };
    hub.Sensors.Start();
    await Task.Delay(3200);
    var readings = hub.Sensors.LatestReadings;
    var latestCount = readings.Count;                 // 必须在 Stop() 之前取值
    var kinds = string.Join(",", readings.Select(r => r.Kind).Distinct());
    var lastError = hub.SensorMonitor.LastError;
    var isRunning = hub.Sensors.IsRunning;
    hub.Sensors.Stop();
    return $"snapshots={count}, snapshotReadings={snapReadings}, latest={latestCount}, running={isRunning}, kinds={kinds}, lastError={lastError ?? "(none)"}";
});
await Test("Hardware.OsCaption + VramSource", async () =>
{
    var o = await hub.Hardware.GetOverviewAsync();
    var build = o.OperatingSystem.BuildNumber.Split('.')[0];
    var isWin11 = int.TryParse(build, out var b) && b >= 22000;
    // Windows 11 上 Caption 不应再残留 "Windows 10"（修复前会拼上 ProductName 遗留值）。
    if (isWin11 && o.OperatingSystem.Caption.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("stale Windows 10 caption: " + o.OperatingSystem.Caption);
    var gpus = string.Join(", ", o.Gpus.Select(g => $"{g.Name}={g.VramSource}/{g.VramTotalBytes}"));
    return $"caption={o.OperatingSystem.Caption}; gpus=[{gpus}]";
});
await Test("Processes.Start + 3s", async () =>
{
    var count = 0;
    var last = 0;
    hub.Processes.SnapshotUpdated += s => { Interlocked.Increment(ref count); last = s.Count; };
    hub.Processes.Start();
    await Task.Delay(3200);
    var err = ((ProcessMonitor)hub.Processes).LastError;
    hub.Processes.Stop();
    return $"snapshots={count}, lastSnapshot={last} processes, lastError={err ?? "(none)"}";
});
var pid = Environment.ProcessId;
await TestSync($"ProcessService.GetModules({pid})", () => $"{hub.ProcessService.GetModules(pid).Count} modules");
await TestSync($"ProcessService.GetHandles({pid})", () => $"{hub.ProcessService.GetHandles(pid).Count} handles");

Emit("");
Emit("## 四、清理扫描（只读，不删除）");
ScanResult? scan = null;
await Test("Clean.ScanAsync(default)", async () =>
{
    scan = await hub.Clean.ScanAsync(new ScanOptions());
    return $"groups={scan.Groups.Count}, items={scan.TotalItemCount}, bytes={scan.TotalSizeBytes}, skipped={scan.SkippedByWhitelistCount}, warnings={scan.Warnings.Count}: {string.Join(" | ", scan.Warnings.Take(2))}";
});
await Test("Clean.ScanAsync(all categories)", async () =>
{
    var all = await hub.Clean.ScanAsync(new ScanOptions
    {
        ScanSystemJunk = true, ScanBrowserCache = true, ScanBrowserHistory = true,
        ScanBrowserCookies = true, ScanBrowserDownloads = true, ScanApplicationCache = true,
        ScanRegistryResidue = true
    });
    return $"groups={all.Groups.Count}, items={all.TotalItemCount}, categories={string.Join(",", all.Groups.Select(g => g.Category))}";
});
// 注册表备份会调用 reg.exe。部分安全策略禁止该程序，故默认跳过；
// 在允许 reg.exe 的本机环境设置 SA_PROBE_REGISTRY=1 即可纳入回归测试。
if (Environment.GetEnvironmentVariable("SA_PROBE_REGISTRY") == "1")
{
    await Test("Clean.BackupRegistryAsync", async () =>
    {
        var r = await hub.Clean.BackupRegistryAsync(temp);
        return $"success={r.Success}, errors={r.Errors.Count}: {string.Join(" | ", r.Errors.Take(2))}, dir={r.Directory}";
    });
}
else
{
    Emit("[SKIP] Clean.BackupRegistryAsync: 需要 reg.exe，当前环境按策略跳过（设置 SA_PROBE_REGISTRY=1 启用）");
}

Emit("");
Emit("## 五、系统优化信息（只读）");
await Test("Startup.GetStartupItemsAsync", async () => $"{((await hub.Startup.GetStartupItemsAsync()).Count)} items");
await Test("Services.GetServicesAsync", async () =>
{
    var s = await hub.Services.GetServicesAsync();
    return $"{s.Count} services, samples=" + string.Join("; ", s.Take(2).Select(x => $"{x.ServiceName}={x.StartMode}/{x.State}"));
});
await TestSync("Tuning.GetTuningItems", () => string.Join("; ", hub.Tuning.GetTuningItems().Select(x => $"{x.Id}:{x.Hive}:{x.CurrentValue}")));

Emit("");
Emit("## 六、工具箱与网络（只读）");
var sampleDir = Path.Combine(Repo, "scripts");
await Test("Toolbox.FindLargeFilesAsync", async () =>
{
    var r = await hub.Toolbox.FindLargeFilesAsync(new LargeFileScanOptions { RootPath = sampleDir, MinimumSizeBytes = 0 });
    return $"{r.Count} files in scripts/";
});
await Test("Toolbox.FindDuplicatesAsync", async () =>
{
    var r = await hub.Toolbox.FindDuplicatesAsync(new DuplicateScanOptions { RootPath = sampleDir, MinimumSizeBytes = 0 });
    return $"{r.Count} duplicate groups in scripts/";
});
await Test("Network.TestLatencyAsync(127.0.0.1)", async () =>
{
    var r = await hub.Network.TestLatencyAsync("127.0.0.1");
    return $"host={r.Host}, avg={r.AverageLatencyMs}ms, loss={r.PacketLossPercent}%";
});
await Test("Network.TestLatencyAsync(-t) [should reject]", async () =>
{
    var r = await hub.Network.TestLatencyAsync("-t");
    return $"host={r.Host}, avg={r.AverageLatencyMs}ms (no exception)";
});
await Test("Update.CheckAsync", async () =>
{
    var r = await hub.Update.CheckAsync();
    return $"current={r.CurrentVersion}, hasUpdate={r.HasUpdate}, message={r.Message}";
});

Emit("");
Emit("## 七、安全护栏（只校验，不执行写操作）");
await TestSync("PathGuard system path", () =>
    PathGuard.IsSafeToDelete(@"C:\Windows\System32\kernel32.dll", out var why) ? "UNSAFE: allowed" : $"blocked: {why}");
await TestSync("PathGuard user temp file", () =>
    PathGuard.IsSafeToDelete(Path.Combine(Path.GetTempPath(), "probe.txt"), out var why) ? "allowed" : $"blocked: {why}");
await Test("Services.SetStartModeAsync(invalid name) [should reject]", async () =>
{
    var r = await hub.Services.SetStartModeAsync("bad/name", ServiceStartMode.Manual);
    return $"{JsonSerializer.Serialize(r)}";
});
await Test("ProcessService.KillAsync(-1) [should reject]", async () =>
{
    var r = await hub.ProcessService.KillAsync(-1, false);
    return $"{JsonSerializer.Serialize(r)}";
});

Emit("");
Emit("## 汇总");
var passed = results.Count(x => x.Ok);
Emit($"PASS {passed} / {results.Count}");
foreach (var (name, ok, detail) in results.Where(x => !x.Ok))
    Emit($"FAILED: {name} -> {detail.Split('\n')[0]}");

try { Directory.Delete(temp, true); } catch { /* best effort */ }
hub.Dispose();

var outPath = Path.Combine(Repo, "artifacts", "functional-test-report.txt");
File.WriteAllText(outPath, report.ToString(), Encoding.UTF8);
Console.WriteLine($"\nReport: {outPath}");
