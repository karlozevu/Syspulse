namespace SystemAssistant.Models.Processes;

/// <summary>进程的一次采样快照。</summary>
public sealed class ProcessSnapshot
{
    public int ProcessId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;

    /// <summary>CPU 占用率（0~100，单核为 100 基准，可超过 100）。</summary>
    public double CpuPercent { get; init; }

    public long WorkingSetBytes { get; init; }

    /// <summary>磁盘读速率（字节/秒）。</summary>
    public double DiskReadBytesPerSecond { get; init; }

    /// <summary>磁盘写速率（字节/秒）。</summary>
    public double DiskWriteBytesPerSecond { get; init; }

    public DateTime StartTime { get; init; }
    public int ThreadCount { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
}

/// <summary>进程加载模块。</summary>
public sealed class ProcessModuleInfo
{
    public string Name { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long MemorySizeBytes { get; init; }
    public string Version { get; init; } = string.Empty;
}

/// <summary>进程持有的文件句柄（尽力而为，系统保护进程可能无法枚举）。</summary>
public sealed class ProcessHandleInfo
{
    public long HandleValue { get; init; }
    public string ObjectName { get; init; } = string.Empty;
    public string ObjectType { get; init; } = string.Empty;
}
