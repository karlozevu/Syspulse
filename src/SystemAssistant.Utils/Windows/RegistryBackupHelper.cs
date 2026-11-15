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
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
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
            if (result.Succeeded && File.Exists(output)) files.Add(output);
            else errors.Add($"{key}：{result.StandardError.Trim()}");
        }

        var manifest = Path.Combine(directory, "RESTORE_README.txt");
        var builder = new StringBuilder();
        builder.AppendLine("System Assistant 注册表备份");
        builder.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine("如需还原，请以管理员身份打开命令提示符，逐个执行：");
        foreach (var file in files) builder.AppendLine($"    reg import \"{file}\"");
        builder.AppendLine();
        builder.AppendLine("注意：导入整个 Classes 键会覆盖此后新建的文件关联，请仅在确认需要时执行。");
        if (errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("以下键导出失败（可能不存在或无权限）：");
            foreach (var error in errors) builder.AppendLine("    " + error);
        }
        ExportHelper.WriteText(manifest, builder.ToString());

        return new RegistryBackupResult
        {
            // 部分键不存在属于正常情况；只要有键成功导出且错误数少于总数即视为可用
            Success = files.Count > 0,
            Directory = directory,
            ManifestPath = manifest,
            Files = files,
            Errors = errors
        };
    }
}
