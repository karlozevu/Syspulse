using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SystemAssistant.Utils.Diagnostics;

/// <summary>极简文件日志提供器（Microsoft.Extensions.Logging 可选依赖的实现）。</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new();
    private readonly Task _worker;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel = LogLevel.Information)
    {
        _path = path;
        _minLevel = minLevel;
        _worker = Task.Run(WriteLoop);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(LogLevel level, string message)
    {
        if (level < _minLevel) return;
        try { _queue.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}"); }
        catch (InvalidOperationException) { /* 已停止写入 */ }
    }

    private int _writeFailures;

    private void WriteLoop()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch (IOException) { }
        }
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 全局异常兜底依赖此日志：主路径失败（占用/磁盘满/权限）时写备用文件，
                // 避免"记录器失效导致异常治理整体失效"。备用也失败才彻底放弃（不能让日志线程崩溃）。
                Interlocked.Increment(ref _writeFailures);
                try
                {
                    var fallback = _path + ".bak";
                    File.AppendAllText(fallback,
                        line + Environment.NewLine + $"[logger] primary failed: {ex.Message}" + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch { /* 备用也失败：无能为力，但不能崩掉采集线程 */ }
            }
        }
    }

    /// <summary>主日志写入失败次数（供诊断：>0 说明日志降级到 .bak）。</summary>
    public int WriteFailures => Volatile.Read(ref _writeFailures);

    public void Dispose()
    {
        _queue.CompleteAdding();
        // 等待后台把剩余日志写完再释放队列：原实现超时后立刻 Dispose 队列，
        // 正在消费的 worker 会抛异常并丢弃积压日志，诊断信息不完整。
        // 写日志是本地追加、耗时极短，5 秒上限足够，超时应属异常情况。
        try { _worker.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* 写入异常已在 WriteLoop 内逐条吞掉 */ }
        _queue.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            _owner.Enqueue(logLevel, $"[{_category}] {text}" + (exception is null ? "" : " | " + exception.Message));
        }
    }
}
