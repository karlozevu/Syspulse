using System.Text.Json.Serialization;

namespace SystemAssistant.Models.Common;

/// <summary>操作类型。所有删除、修改、系统调用都必须落日志。</summary>
public enum OperationType
{
    Clean,
    Registry,
    Service,
    Startup,
    SystemTuning,
    SystemCommand,
    Process,
    File,
    Settings,
    Security
}

/// <summary>一条操作日志。</summary>
public sealed class OperationLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OperationType Type { get; init; }

    public string Action { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>通用操作结果。</summary>
public sealed class OperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public Exception? Exception { get; init; }

    public static OperationResult Ok(string message = "") => new() { Success = true, Message = message };
    public static OperationResult Fail(string message, Exception? ex = null)
        => new() { Success = false, Message = message, Exception = ex };
}

/// <summary>应用版本信息。</summary>
public sealed class AppVersionInfo
{
    public string CurrentVersion { get; init; } = "1.1.3";
    public string LatestVersion { get; init; } = string.Empty;
    public bool HasUpdate { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string DownloadPage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
