using System.Diagnostics;
using System.Net.NetworkInformation;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Health;

namespace SystemAssistant.Core.Health;

/// <summary>
/// 实时网络监控：基于 NetworkInterface 累计字节数的差值计算上下行速率，
/// 后台定时采样并通过事件推送曲线数据；同时提供接口列表与累计流量快照。
/// </summary>
public sealed class NetworkMonitorService : INetworkMonitorService, IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _stopping;
    private volatile bool _disposed;
    private readonly Dictionary<string, (long Received, long Sent, long Tick)> _previous = new();

    public event Action<IReadOnlyList<NetworkSample>>? SampleUpdated;

    public bool IsRunning => !_disposed && !_stopping && _worker is { IsCompleted: false };
    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>最近一次采样的接口列表。</summary>
    public IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; private set; } = Array.Empty<NetworkInterfaceInfo>();

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _stopping || _worker is { IsCompleted: false }) return;
            SeedCounters();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => RunLoopAsync(token), token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
            cts?.Cancel();
        }
        try { try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { } cts?.Dispose(); }
        finally { lock (_sync) _stopping = false; }
    }

    /// <summary>获取接口静态信息 + 累计流量（普通权限可用）。</summary>
    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces()
    {
        var list = new List<NetworkInterfaceInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var stats = ni.GetIPv4Statistics();
                list.Add(new NetworkInterfaceInfo
                {
                    Name = ni.Name,
                    Type = ni.NetworkInterfaceType.ToString(),
                    Status = ni.OperationalStatus.ToString(),
                    SpeedBitsPerSecond = ni.Speed,
                    TotalReceivedBytes = stats.BytesReceived,
                    TotalSentBytes = stats.BytesSent,
                    IsActive = ni.OperationalStatus == OperationalStatus.Up
                               && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                });
            }
        }
        catch { /* 枚举失败返回已取到的部分 */ }
        Interfaces = list;
        return list;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(RefreshIntervalMs, 250, 60_000)));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                CollectOnce();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 采集循环异常不外抛 */ }
    }

    private void SeedCounters()
    {
        lock (_sync)
        {
            _previous.Clear();
            var tick = Stopwatch.GetTimestamp();
            try
            {
                foreach (var ni in ActiveInterfaces())
                    _previous[ni.Id] = (ni.GetIPv4Statistics().BytesReceived, ni.GetIPv4Statistics().BytesSent, tick);
            }
            catch { }
        }
    }

    private void CollectOnce()
    {
        var samples = new List<NetworkSample>();
        var tick = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            try
            {
                double down = 0, up = 0;
                foreach (var ni in ActiveInterfaces())
                {
                    var stats = ni.GetIPv4Statistics();
                    if (_previous.TryGetValue(ni.Id, out var old))
                    {
                        var seconds = (tick - old.Tick) * 1.0 / Stopwatch.Frequency;
                        if (seconds > 0)
                        {
                            // 计数器回退（网卡重置）时按 0 处理，避免负值速率
                            if (stats.BytesReceived >= old.Received)
                                down += (stats.BytesReceived - old.Received) / seconds;
                            if (stats.BytesSent >= old.Sent)
                                up += (stats.BytesSent - old.Sent) / seconds;
                        }
                    }
                    _previous[ni.Id] = (stats.BytesReceived, stats.BytesSent, tick);
                }
                samples.Add(new NetworkSample
                {
                    DownloadBytesPerSecond = Math.Max(0, down),
                    UploadBytesPerSecond = Math.Max(0, up)
                });
            }
            catch { /* 单次采样失败跳过本轮 */ }
        }
        GetInterfaces(); // 刷新接口快照
        var handlers = SampleUpdated;
        if (handlers is null) return;
        foreach (Delegate h in handlers.GetInvocationList())
        {
            try { ((Action<IReadOnlyList<NetworkSample>>)h)(samples); }
            catch { /* 订阅者异常不影响采集 */ }
        }
    }

    private static IEnumerable<NetworkInterface> ActiveInterfaces()
        => NetworkInterface.GetAllNetworkInterfaces().Where(ni =>
            ni.OperationalStatus == OperationalStatus.Up
            && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
