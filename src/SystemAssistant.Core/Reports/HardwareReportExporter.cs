using System.Text;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.IO;

namespace SystemAssistant.Core.Reports;

/// <summary>整机硬件报告导出：TXT / Markdown / HTML 三种格式，内容完全一致。</summary>
public sealed class HardwareReportExporter : IHardwareReportExporter
{
    public string BuildPlainText(SystemOverview overview)
    {
        var builder = new StringBuilder();
        builder.AppendLine("==========================================================");
        builder.AppendLine("              System Assistant 整机硬件报告");
        builder.AppendLine($"生成时间：{overview.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine("==========================================================");
        builder.AppendLine();
        foreach (var section in BuildSections(overview))
        {
            builder.AppendLine($"【{section.Title}】");
            foreach (var (key, value) in section.Rows)
                builder.AppendLine($"  {key,-18}: {value}");
            builder.AppendLine();
        }
        builder.AppendLine("----------------------------------------------------------");
        builder.AppendLine("说明：本报告仅在本机生成，不会被上传。传感器数据为生成时刻的快照。");
        return builder.ToString();
    }

    public string BuildMarkdown(SystemOverview overview)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# System Assistant 整机硬件报告");
        builder.AppendLine();
        builder.AppendLine($"> 生成时间：{overview.GeneratedAt:yyyy-MM-dd HH:mm:ss}  ");
        builder.AppendLine("> 本报告仅在本机生成，不会被上传。");
        builder.AppendLine();
        foreach (var section in BuildSections(overview))
        {
            builder.AppendLine($"## {section.Title}");
            builder.AppendLine();
            builder.AppendLine("| 项目 | 值 |");
            builder.AppendLine("| --- | --- |");
            foreach (var (key, value) in section.Rows)
                builder.AppendLine($"| {EscapeMarkdown(key)} | {EscapeMarkdown(value)} |");
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public string BuildHtml(SystemOverview overview)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\" />");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine("<title>System Assistant 整机硬件报告</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#f6f8fa;color:#1f2328;margin:0;padding:32px}");
        builder.AppendLine(".wrap{max-width:980px;margin:auto;background:#fff;border:1px solid #d8dee4;border-radius:10px;padding:32px}");
        builder.AppendLine("h1{font-size:24px;margin:0 0 6px}h2{font-size:17px;margin:28px 0 10px;padding-bottom:6px;border-bottom:2px solid #0969da;color:#0969da}");
        builder.AppendLine(".meta{color:#656d76;font-size:13px;margin-bottom:20px}");
        builder.AppendLine("table{width:100%;border-collapse:collapse;font-size:14px}");
        builder.AppendLine("th,td{text-align:left;padding:8px 10px;border-bottom:1px solid #eaeef2}");
        builder.AppendLine("th{width:190px;color:#57606a;font-weight:600;background:#f6f8fa}");
        builder.AppendLine(".warn{color:#bf8700;font-weight:600}.bad{color:#cf222e;font-weight:600}.ok{color:#1a7f37}");
        builder.AppendLine("footer{margin-top:28px;color:#656d76;font-size:12px}");
        builder.AppendLine("</style></head><body><div class=\"wrap\">");
        builder.AppendLine("<h1>System Assistant 整机硬件报告</h1>");
        builder.AppendLine($"<div class=\"meta\">生成时间：{overview.GeneratedAt:yyyy-MM-dd HH:mm:ss} · 仅在本机生成，不会上传</div>");

        foreach (var section in BuildSections(overview))
        {
            builder.AppendLine($"<h2>{ExportHelper.HtmlEncode(section.Title)}</h2>");
            builder.AppendLine("<table>");
            foreach (var (key, value) in section.Rows)
            {
                var css = value.Contains("警告") || value.Contains("⚠") ? " class=\"warn\""
                    : value.Contains("故障") ? " class=\"bad\""
                    : value == "健康" ? " class=\"ok\"" : string.Empty;
                builder.AppendLine($"<tr><th>{ExportHelper.HtmlEncode(key)}</th><td{css}>{ExportHelper.HtmlEncode(value)}</td></tr>");
            }
            builder.AppendLine("</table>");
        }
        builder.AppendLine("<footer>传感器数据为生成时刻的快照；SMART 健康状态来自 Windows 存储子系统，仅供参考。</footer>");
        builder.AppendLine("</div></body></html>");
        return builder.ToString();
    }

    /// <summary>按格式导出到目录，返回生成的文件完整路径。</summary>
    public string Export(SystemOverview overview, string directory, string format)
    {
        Directory.CreateDirectory(directory);
        var normalized = format.TrimStart('.').ToLowerInvariant();
        var (extension, content) = normalized switch
        {
            "md" or "markdown" => ("md", BuildMarkdown(overview)),
            "html" or "htm" => ("html", BuildHtml(overview)),
            _ => ("txt", BuildPlainText(overview))
        };
        var path = Path.Combine(directory, ExportHelper.BuildReportFileName("SystemReport", extension, overview.GeneratedAt));
        ExportHelper.WriteText(path, content);
        return path;
    }

    private static IEnumerable<(string Title, IReadOnlyList<(string Key, string Value)> Rows)> BuildSections(SystemOverview o)
    {
        var os = o.OperatingSystem;
        yield return ("操作系统", new List<(string, string)>
        {
            ("系统", os.Caption), ("版本", os.Version), ("内部版本", os.BuildNumber),
            ("架构", os.Architecture), ("安装时间", os.InstallDate), ("最近启动", os.LastBootTime),
            ("计算机名", os.ComputerName), ("当前用户", os.UserName)
        });

        yield return ("主板与 BIOS", new List<(string, string)>
        {
            ("主板厂商", o.Motherboard.Manufacturer), ("主板型号", o.Motherboard.Model),
            ("BIOS 厂商", o.Bios.Manufacturer), ("BIOS 版本", o.Bios.Version),
            ("BIOS 日期", o.Bios.ReleaseDate)
        });

        var cpu = o.Cpu;
        yield return ("处理器", new List<(string, string)>
        {
            ("型号", cpu.Name), ("制造商", cpu.Manufacturer), ("插槽", cpu.Socket),
            ("物理核心", cpu.PhysicalCores.ToString()), ("逻辑处理器", cpu.LogicalProcessors.ToString()),
            ("基础频率", $"{cpu.BaseClockMHz:F0} MHz"),
            ("最大睿频", cpu.MaxTurboMHz > 0 ? $"{cpu.MaxTurboMHz:F0} MHz" : "N/A（WMI 不提供）"),
            ("L1 缓存", $"{cpu.L1CacheKB} KB"), ("L2 缓存", $"{cpu.L2CacheKB} KB"), ("L3 缓存", $"{cpu.L3CacheKB} KB"),
            ("当前占用率", $"{cpu.UsagePercent:F1} %"), ("实时频率", $"{cpu.CurrentClockMHz:F0} MHz"),
            ("温度", cpu.TemperatureCelsius.HasValue ? $"{cpu.TemperatureCelsius:F1} °C" : "N/A（需传感器）")
        });

        var memory = o.Memory;
        var memoryRows = new List<(string, string)>
        {
            ("总容量", FormatHelper.FormatBytes(memory.TotalBytes)),
            ("已使用", FormatHelper.FormatBytes(memory.UsedBytes)),
            ("可用", FormatHelper.FormatBytes(memory.AvailableBytes)),
            ("占用率", $"{memory.UsagePercent:F1} %"),
            ("分页已用", FormatHelper.FormatBytes(memory.PageFileUsedBytes)),
            ("分页总量", FormatHelper.FormatBytes(memory.PageFileTotalBytes)),
            ("通道 / 插槽", memory.ChannelDescription)
        };
        for (var i = 0; i < memory.Modules.Count; i++)
        {
            var module = memory.Modules[i];
            memoryRows.Add(($"内存条 #{i + 1}",
                $"{module.Manufacturer} {FormatHelper.FormatBytes(module.CapacityBytes)} @ {module.SpeedMHz} MHz ({module.Slot})"));
        }
        yield return ("内存", memoryRows);

        var gpuRows = new List<(string, string)>();
        for (var i = 0; i < o.Gpus.Count; i++)
        {
            var gpu = o.Gpus[i];
            gpuRows.Add(($"显卡 #{i + 1}", gpu.Name));
            gpuRows.Add(("驱动版本", gpu.DriverVersion));
            gpuRows.Add(("显存", FormatHelper.FormatBytes(gpu.VramTotalBytes)
                + (gpu.VramUsedBytes.HasValue ? $" / 已用 {FormatHelper.FormatBytes(gpu.VramUsedBytes.Value)}" : "")));
            gpuRows.Add(("负载", gpu.LoadPercent.HasValue ? $"{gpu.LoadPercent:F1} %" : "N/A"));
            gpuRows.Add(("核心温度", gpu.TemperatureCelsius.HasValue ? $"{gpu.TemperatureCelsius:F1} °C" : "N/A"));
        }
        if (gpuRows.Count == 0) gpuRows.Add(("显卡", "未检测到"));
        yield return ("显卡", gpuRows);

        var diskRows = new List<(string, string)>();
        foreach (var disk in o.Disks)
        {
            diskRows.Add(($"磁盘 {disk.DiskIndex}", $"{disk.Model} · {FormatHelper.FormatBytes(disk.TotalBytes)} · {disk.MediaTypeText}"));
            diskRows.Add(("健康状态", HealthText(disk.Health) + " / " + disk.Smart.Summary));
            diskRows.Add(("SMART", disk.Smart.IsAvailable
                ? $"通电 {(disk.Smart.PowerOnHours?.ToString() ?? "N/A")} 小时，循环 {(disk.Smart.PowerCycleCount?.ToString() ?? "N/A")} 次"
                : "不可用"));
            foreach (var partition in disk.Partitions)
            {
                diskRows.Add(($"分区 {partition.DriveLetter}",
                    $"{partition.VolumeLabel} · {partition.FileSystem} · 已用 {FormatHelper.FormatBytes(partition.UsedBytes)} / {FormatHelper.FormatBytes(partition.TotalBytes)} ({partition.UsedPercent:F1} %)"));
            }
        }
        if (diskRows.Count == 0) diskRows.Add(("磁盘", "未检测到"));
        yield return ("磁盘", diskRows);

        var networkRows = o.NetworkAdapters.Select(a =>
            (a.Name, $"{a.MacAddress} · IP {a.IpAddresses} · {a.Speed}")).ToList();
        if (networkRows.Count == 0) networkRows.Add(("网卡", "未检测到"));
        yield return ("网卡", networkRows);

        if (o.Battery.IsPresent)
        {
            yield return ("电池", new List<(string, string)>
            {
                ("设计容量", $"{o.Battery.DesignCapacityMwh} mWh"),
                ("当前满充容量", $"{o.Battery.FullChargeCapacityMwh} mWh"),
                ("电池损耗", $"{o.Battery.WearPercent:F1} %"),
                ("循环次数", o.Battery.CycleCount >= 0 ? o.Battery.CycleCount.ToString() : "N/A"),
                ("剩余电量", $"{o.Battery.ChargePercent:F1} %")
            });
        }

        var displayRows = o.Displays.Select(d =>
            (d.DeviceName, $"{d.ResolutionText} · {d.BitsPerPixel} bit · HDR {(d.HdrEnabled switch { true => "开启", false => "关闭", _ => "未知" })}")).ToList();
        if (displayRows.Count == 0) displayRows.Add(("显示器", "未检测到"));
        yield return ("显示器", displayRows);
    }

    private static string HealthText(DiskHealthStatus status) => status switch
    {
        DiskHealthStatus.Healthy => "健康",
        DiskHealthStatus.Warning => "⚠ 警告",
        DiskHealthStatus.Failed => "故障",
        _ => "未知"
    };

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
