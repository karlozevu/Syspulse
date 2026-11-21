using Microsoft.Win32;
using System.Text;
using SystemAssistant.Utils.Diagnostics;
using SystemAssistant.Utils.IO;

namespace SystemAssistant.Utils.Windows;

/// <summary>注册表备份结果。</summary>
public sealed class RegistryBackupResult
{
    public bool Success { get; init; }
    public string Directory { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    /// <summary>阻断性失败（权限不足、reg.exe 不可用等），会导致 Success=false。</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    /// <summary>可预期的跳过（目标键本就不存在），不影响 Success。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 注册表修改前的自动备份。使用系统自带 reg.exe export，不写入任何值。
/// 只导出本次可能被改动的相关键，避免导出整个注册表造成耗时与体积过大。
/// </summary>
public static class RegistryBackupHelper
{
    /// <summary>默认备份的注册表键（卸载残留、文件关联、启动项、App Paths）。</summary>
    public static IReadOnlyList<string> DefaultKeys { get; } = new[]
    {
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\Software\Classes",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
    };

    /// <summary>
    /// 把指定键导出到 targetDirectory 下以时间戳命名的子目录，并生成还原说明。
    /// </summary>
    public static async Task<RegistryBackupResult> BackupAsync(
        IEnumerable<string>? keys, string targetDirectory, CancellationToken token = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var files = new List<string>();
        string directory;
        try
        {
            directory = Path.Combine(targetDirectory, "RegBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new RegistryBackupResult { Success = false, Errors = new[] { "无法创建备份目录：" + ex.Message } };
        }

        var index = 0;
        foreach (var key in (keys ?? DefaultKeys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (token.IsCancellationRequested) break;
            index++;
            var fileName = $"{index:00}_{ExportHelper.SanitizeFileName(key.Replace('\\', '_'))}.reg";
            var output = Path.Combine(directory, fileName);
            var result = await CommandRunner.RunQuickAsync("reg.exe", $"export \"{key}\" \"{output}\" /y", token);
            if (result.Succeeded && File.Exists(output))
            {
                files.Add(output);
                continue;
            }
            var reason = string.IsNullOrWhiteSpace(result.StandardError)
                ? "reg.exe 导出失败或未生成文件"
                : result.StandardError.Trim();
            var detail = $"{key}：{reason}";
            // 「目标键不存在」是常见且可预期的情况（32/64 位视图差异、从未安装过相关软件等），
            // 记为警告而非阻断性错误；权限不足、reg.exe 被策略拦截等仍为错误。
            // 避免单个无关键把整个备份判为失败，进而永久阻断注册表删除。
            if (!KeyExists(key)) warnings.Add(detail + "（目标键不存在，已跳过）");
            else errors.Add(detail);
        }

        var manifest = Path.Combine(directory, "RESTORE_README.txt");
        var builder = new StringBuilder();
        builder.AppendLine("Syspulse 注册表备份");
        builder.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine("如需还原，请以管理员身份打开命令提示符，逐个执行：");
        foreach (var file in files) builder.AppendLine($"    reg import \"{file}\"");
        builder.AppendLine();
        builder.AppendLine("注意：导入整个 Classes 键会覆盖此后新建的文件关联，请仅在确认需要时执行。");
        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("以下键未导出（目标键不存在，可忽略）：");
            foreach (var warning in warnings) builder.AppendLine("    " + warning);
        }
        if (errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("以下键导出失败（权限不足或 reg.exe 不可用，属于阻断性错误）：");
            foreach (var error in errors) builder.AppendLine("    " + error);
        }
        ExportHelper.WriteText(manifest, builder.ToString());

        return new RegistryBackupResult
        {
            // 成功条件：至少成功导出一个键，且不存在阻断性错误（Errors）。
            // 「键不存在」已归入 Warnings，不再因为单个无关键把整体判为失败；
            // 失败 / 跳过明细分别通过 Errors / Warnings 回传给调用方展示。
            Success = files.Count > 0 && errors.Count == 0,
            Directory = directory,
            ManifestPath = manifest,
            Files = files,
            Errors = errors,
            Warnings = warnings
        };
    }

    /// <summary>
    /// 用只读方式判断目标键是否真实存在，用于把「键不存在」与「权限 / reg.exe 不可用」区分开。
    /// 无法判断时按「可能存在」处理（返回 true），避免把权限问题误归类为可忽略的警告。
    /// </summary>
    private static bool KeyExists(string fullKey)
    {
        var parts = fullKey.Split('\\', 2);
        if (parts.Length < 2) return false;
        RegistryHive hive;
        if (parts[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.LocalMachine;
        else if (parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.CurrentUser;
        else return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(parts[1]);
            return key is not null;
        }
        catch
        {
            return true;
        }
    }
}
