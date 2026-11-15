using System.Diagnostics;
using Microsoft.Win32;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Optimization;

namespace SystemAssistant.Core.Optimization;

/// <summary>
/// 启动项管理：读取注册表 Run / RunOnce 与所有用户的启动文件夹。
/// 启用 / 禁用通过 Windows 官方的 StartupApproved 机制实现（不直接删除原值），
/// 保证可以随时恢复；删除操作会把快捷方式移入回收站而不是直接抹掉。
/// </summary>
public sealed class StartupItemService : IStartupItemService
{
    private readonly IOperationLogService _log;

    private static readonly (string Hive, string SubKey, string ApprovedKey)[] RunKeys =
    {
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\RunOnce"),
        ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32")
    };

    public StartupItemService(IOperationLogService log) => _log = log;

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken token = default)
        => Task.Run(() => (IReadOnlyList<StartupItem>)ReadAll(token), token);

    private IReadOnlyList<StartupItem> ReadAll(CancellationToken token)
    {
        var items = new List<StartupItem>();
        foreach (var (hive, subKey, approvedKey) in RunKeys)
        {
            if (token.IsCancellationRequested) break;
            using var key = Open(hive, subKey, false);
            if (key is null) continue;
            var approved = ReadApproved(hive, approvedKey);
            foreach (var name in key.GetValueNames())
            {
                var command = key.GetValue(name)?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(command)) continue;
                var enabled = !approved.TryGetValue(name, out var state) || state;
                items.Add(new StartupItem
                {
                    Id = $"REG|{hive}|{subKey}|{name}",
                    Name = name,
                    Command = command,
                    Publisher = GetPublisher(command),
                    Source = subKey.Contains("RunOnce", StringComparison.OrdinalIgnoreCase)
                        ? StartupSource.RegistryRunOnce : StartupSource.RegistryRun,
                    IsEnabled = enabled,
                    Location = $"{hive}\\{subKey}",
                    RiskHint = hive == "HKLM" ? "系统级启动项，修改需要管理员权限" : string.Empty
                });
            }
        }

        foreach (var folder in StartupFolders())
        {
            if (token.IsCancellationRequested) break;
            if (!Directory.Exists(folder)) continue;
            var approved = ReadApproved("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    var enabled = !approved.TryGetValue(name, out var state) || state;
                    items.Add(new StartupItem
                    {
                        Id = $"FOLDER|{file}",
                        Name = Path.GetFileNameWithoutExtension(file),
                        Command = file,
                        Publisher = GetPublisher(file),
                        Source = StartupSource.StartupFolder,
                        IsEnabled = enabled,
                        Location = folder,
                        RiskHint = string.Empty
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return items.OrderBy(i => i.Source).ThenBy(i => i.Name).ToArray();
    }

    public Task<OperationResult> SetEnabledAsync(StartupItem item, bool enabled, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (item.Source == StartupSource.StartupFolder)
                {
                    return WriteApproved("HKCU",
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
                        Path.GetFileName(item.Command), enabled, "启动文件夹项", item.Name);
                }

                var parts = item.Id.Split('|');
                if (parts.Length < 4) return OperationResult.Fail("无法解析启动项标识");
                var hive = parts[1];
                var subKey = parts[2];
                var name = string.Join('|', parts.Skip(3));
                var approvedKey = subKey.Contains("RunOnce", StringComparison.OrdinalIgnoreCase)
                    ? (hive == "HKLM"
                        ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\RunOnce"
                        : @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\RunOnce")
                    : subKey.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)
                        ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"
                        : (hive == "HKLM"
                            ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
                            : @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                return WriteApproved(hive, approvedKey, name, enabled, "注册表启动项", item.Name);
            }
            catch (Exception ex)
            {
                _log.Log(OperationType.Startup, enabled ? "启用启动项" : "禁用启动项", item.Name, false, ex.Message);
                return OperationResult.Fail(ex.Message, ex);
            }
        }, token);
    }

    public Task<OperationResult> DeleteAsync(StartupItem item, CancellationToken token = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (item.Source == StartupSource.StartupFolder)
                {
                    var (success, error, _) = SystemAssistant.Utils.IO.FileSystemHelper.DeleteToRecycleBin(item.Command);
                    _log.Log(OperationType.Startup, "删除启动项", item.Command, success, success ? null : error);
                    return success ? OperationResult.Ok("已移入回收站") : OperationResult.Fail(error);
                }

                var parts = item.Id.Split('|');
                if (parts.Length < 4) return OperationResult.Fail("无法解析启动项标识");
                var hive = parts[1];
                var subKey = parts[2];
                var name = string.Join('|', parts.Skip(3));
                using var key = Open(hive, subKey, true);
                if (key is null) return OperationResult.Fail("无法打开注册表键，可能需要管理员权限");
                key.DeleteValue(name, false);
                _log.Log(OperationType.Startup, "删除启动项", $"{hive}\\{subKey}\\{name}", true);
                return OperationResult.Ok();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _log.Log(OperationType.Startup, "删除启动项", item.Name, false, ex.Message);
                return OperationResult.Fail(ex.Message, ex);
            }
        }, token);
    }

    private OperationResult WriteApproved(string hive, string approvedKey, string valueName,
        bool enabled, string kind, string displayName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(approvedKey, true);
        if (key is null) return OperationResult.Fail("无法创建 StartupApproved 键，可能需要管理员权限");
        // Windows 规定：首字节 2 = 启用，3 = 禁用，其余字节保留为 0
        key.SetValue(valueName, new byte[] { enabled ? (byte)2 : (byte)3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            RegistryValueKind.Binary);
        _log.Log(OperationType.Startup, enabled ? "启用启动项" : "禁用启动项",
            $"{kind}：{displayName}", true);
        return OperationResult.Ok();
    }

    private static Dictionary<string, bool> ReadApproved(string hive, string approvedKey)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var key = Open(hive, approvedKey, false);
        if (key is null) return map;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is byte[] { Length: > 0 } bytes)
                map[name] = bytes[0] != 3;
        }
        return map;
    }

    private static IEnumerable<string> StartupFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
    }

    private static string GetPublisher(string command)
    {
        try
        {
            var path = command.Trim();
            if (path.StartsWith('"'))
            {
                var end = path.IndexOf('"', 1);
                path = end > 1 ? path[1..end] : path.Trim('"');
            }
            else
            {
                var space = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (space > 0) path = path[..(space + 4)];
            }
            if (!Path.IsPathRooted(path)) path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path)) return string.Empty;
            return FileVersionInfo.GetVersionInfo(path).CompanyName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static RegistryKey? Open(string hive, string subKey, bool writable)
    {
        try
        {
            // baseKey 无论是否成功打开子键都要释放；返回的子键持有独立句柄，由调用方 using 释放
            using var baseKey = RegistryKey.OpenBaseKey(
                hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            return writable ? baseKey.OpenSubKey(subKey, true) : baseKey.OpenSubKey(subKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
