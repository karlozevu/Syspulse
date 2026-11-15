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

    private void WriteLoop()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
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
