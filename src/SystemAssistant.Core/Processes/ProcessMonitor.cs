using System.Diagnostics;
using System.Management;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Processes;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.Core.Processes;

/// <summary>
/// 实时进程采样器。CPU 占用率由两次 TotalProcessorTime 差值计算，
/// 磁盘速率由 GetProcessIoCounters 差值计算，全部在后台任务中完成。
/// </summary>
public sealed class ProcessMonitor : IProcessMonitor, IDisposable
{
    private readonly Dictionary<int, Sample> _previous = new();
    private readonly Dictionary<int, string> _owners = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public event Action<IReadOnlyList<ProcessSnapshot>>? SnapshotUpdated;

    public int RefreshIntervalMs { get; set; } = 2000;
    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_sync)
        {
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }
        cts?.Cancel();
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        FireAndForget(RefreshOwnersAsync);
        var lastOwnerRefresh = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(RefreshIntervalMs, 500, 60_000)));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Math.Abs(timer.Period.TotalMilliseconds - RefreshIntervalMs) > 1)
                    timer.Period = TimeSpan.FromMilliseconds(Math.Clamp(RefreshIntervalMs, 500, 60_000));

                var snapshot = SampleOnce();
                SnapshotUpdated?.Invoke(snapshot);

                if (DateTime.UtcNow - lastOwnerRefresh > TimeSpan.FromMinutes(1))
                {
                    lastOwnerRefresh = DateTime.UtcNow;
                    FireAndForget(RefreshOwnersAsync);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private IReadOnlyList<ProcessSnapshot> SampleOnce()
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<int, Sample>();
        var result = new List<ProcessSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    var cpuTicks = process.TotalProcessorTime.Ticks;
                    var workingSet = process.WorkingSet64;
                    var startTime = process.StartTime;
                    var threads = process.Threads.Count;
                    string path = string.Empty;
                    try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
                    var (readBytes, writeBytes) = WindowsNativeInfo.GetProcessIoBytes(process.Handle);

                    var sample = new Sample(now, cpuTicks, readBytes, writeBytes);
                    current[pid] = sample;

                    double cpuPercent = 0, readPerSecond = 0, writePerSecond = 0;
                    lock (_sync)
                    {
                        if (_previous.TryGetValue(pid, out var old))
                        {
                            var seconds = Math.Max(0.001, (now - old.Timestamp).TotalSeconds);
                            cpuPercent = (cpuTicks - old.CpuTicks) / TimeSpan.TicksPerSecond / seconds
                                         / Environment.ProcessorCount * 100;
                            readPerSecond = Math.Max(0, (readBytes - old.ReadBytes) / seconds);
                            writePerSecond = Math.Max(0, (writeBytes - old.WriteBytes) / seconds);
                        }
                        _owners.TryGetValue(pid, out var owner);
                        result.Add(new ProcessSnapshot
                        {
                            ProcessId = pid,
                            Name = process.ProcessName,
                            UserName = owner ?? string.Empty,
                            CpuPercent = Math.Clamp(cpuPercent, 0, 100 * Environment.ProcessorCount),
                            WorkingSetBytes = workingSet,
                            DiskReadBytesPerSecond = readPerSecond,
                            DiskWriteBytesPerSecond = writePerSecond,
                            StartTime = startTime,
                            ThreadCount = threads,
                            ExecutablePath = path
                        });
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or NotSupportedException or UnauthorizedAccessException)
                {
                    // 进程在采样期间退出或为受保护进程
                }
            }
        }

        lock (_sync)
        {
            _previous.Clear();
            foreach (var (pid, sample) in current) _previous[pid] = sample;
        }
        return result;
    }

    /// <summary>通过 WMI 批量获取进程所有者，低频刷新并缓存，避免每轮采样都做昂贵查询。</summary>
    private Task RefreshOwnersAsync()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT ProcessId FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var pid = Convert.ToInt32(mo["ProcessId"]);
                    try
                    {
                        using var output = mo.InvokeMethod("GetOwner", null, null);
                        if (output is null || Convert.ToInt32(output["ReturnValue"]) != 0) continue;
                        var domain = output["Domain"]?.ToString();
                        var user = output["User"]?.ToString() ?? string.Empty;
                        var name = string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                        lock (_sync) _owners[pid] = name;
                    }
                    catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception)
        {
            // 进程所有者属于「尽力而为」信息：WMI 在某些系统 / 权限状态下会抛
            // InvalidOperationException，这里统一吞掉并继续采样，绝不产生未观察异常。
        }
        return Task.CompletedTask;
    }

    /// <summary>安全地启动即发即忘的后台任务：任何异常都被观察并记录，避免进程级未观察异常。</summary>
    private static void FireAndForget(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception) { /* 已观察，后台能力失败不影响主流程 */ }
        });
    }

    public void Dispose() => Stop();

    private sealed record Sample(DateTime Timestamp, long CpuTicks, ulong ReadBytes, ulong WriteBytes);
}
