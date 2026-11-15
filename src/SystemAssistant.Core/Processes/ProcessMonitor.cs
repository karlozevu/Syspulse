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
    // 历史键包含 PID + 启动时间，避免 PID 复用时把新进程的计数器当成旧进程做差
    private readonly Dictionary<(int Pid, long StartTicks), Sample> _previous = new();
    private readonly Dictionary<int, string> _owners = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public event Action<IReadOnlyList<ProcessSnapshot>>? SnapshotUpdated;

    public int RefreshIntervalMs { get; set; } = 2000;
    public bool IsRunning => _worker is { IsCompleted: false };

    /// <summary>最近一次采样循环的错误，供界面降级提示（不对外抛出）。</summary>
    public string LastError { get; private set; } = string.Empty;

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
                RaiseSnapshotUpdated(snapshot);

                if (DateTime.UtcNow - lastOwnerRefresh > TimeSpan.FromMinutes(1))
                {
                    lastOwnerRefresh = DateTime.UtcNow;
                    FireAndForget(RefreshOwnersAsync);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 兜底：采样循环里的任何异常都必须被观察并记录，避免变成进程级未观察任务异常
            LastError = "进程采样已停止：" + ex.Message;
        }
    }

    /// <summary>逐个订阅者调用，单个订阅者抛异常不影响其它订阅者与采集循环。</summary>
    private void RaiseSnapshotUpdated(IReadOnlyList<ProcessSnapshot> snapshot)
    {
        var handlers = SnapshotUpdated;
        if (handlers is null) return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<IReadOnlyList<ProcessSnapshot>>)handler)(snapshot); }
            catch (Exception ex) { LastError = "快照订阅者异常：" + ex.Message; }
        }
    }

    private IReadOnlyList<ProcessSnapshot> SampleOnce()
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<(int Pid, long StartTicks), Sample>();
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
                    var startTicks = startTime.Ticks;
                    var threads = process.Threads.Count;
                    string path = string.Empty;
                    try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
                    var (readBytes, writeBytes) = WindowsNativeInfo.GetProcessIoBytes(process.Handle);

                    var key = (Pid: pid, StartTicks: startTicks);
                    var sample = new Sample(now, cpuTicks, readBytes, writeBytes);
                    current[key] = sample;

                    double cpuPercent = 0, readPerSecond = 0, writePerSecond = 0;
                    lock (_sync)
                    {
                        if (_previous.TryGetValue(key, out var old))
                        {
                            var seconds = Math.Max(0.001, (now - old.Timestamp).TotalSeconds);
                            var cpuDelta = Math.Max(0L, cpuTicks - old.CpuTicks);
                            cpuPercent = cpuDelta / TimeSpan.TicksPerSecond / seconds
                                         / Environment.ProcessorCount * 100;
                            // ulong 计数器可能回绕 / 复位，必须先比较再相减，否则会下溢成巨大值
                            var readDelta = readBytes >= old.ReadBytes ? readBytes - old.ReadBytes : 0UL;
                            var writeDelta = writeBytes >= old.WriteBytes ? writeBytes - old.WriteBytes : 0UL;
                            readPerSecond = readDelta / seconds;
                            writePerSecond = writeDelta / seconds;
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
            foreach (var (key, sample) in current) _previous[key] = sample;
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
