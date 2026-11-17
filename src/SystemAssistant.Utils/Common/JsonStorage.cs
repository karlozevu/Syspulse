using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAssistant.Utils.Common;

/// <summary>
/// 基于 System.Text.Json 的轻量持久化工具。写入采用「临时文件 + 原子替换」，
/// 避免进程崩溃导致配置文件损坏。
/// </summary>
public static class JsonStorage
{
    private static readonly object Sync = new();

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>读取 JSON 配置文件；文件不存在或损坏时返回默认值。</summary>
    public static T Load<T>(string path) where T : class, new()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(path)) return new T();
                var json = File.ReadAllText(path);
                return string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
            catch (Exception)
            {
                // 配置损坏时不能让程序无法启动，直接回落到默认配置
                return new T();
            }
        }
    }

    /// <summary>原子写入 JSON 配置文件。</summary>
    public static void Save<T>(string path, T value) where T : class
    {
        lock (Sync)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 临时文件名带 GUID / PID：多实例并发配置写入时不会互相覆盖 .tmp。
            var temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + "_" + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>追加一行 JSON（JSONL 格式，用于操作日志）。</summary>
    public static void AppendLine<T>(string path, T value)
    {
        lock (Sync)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(value, Options).ReplaceLineEndings(" ");
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    /// <summary>逐行读取 JSONL 文件，忽略损坏行。</summary>
    public static IReadOnlyList<T> ReadLines<T>(string path) where T : class
    {
        var list = new List<T>();
        lock (Sync)
        {
            if (!File.Exists(path)) return list;
            // File.ReadLines 是延迟枚举：文件在枚举期间被删 / 锁定会在 MoveNext 抛 IO 异常，
            // 必须把整段枚举纳入保护，否则读取日志会让界面崩溃。
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var item = JsonSerializer.Deserialize<T>(line, Options);
                        if (item is not null) list.Add(item);
                    }
                    catch (JsonException)
                    {
                        // 单行损坏不应影响整个日志的读取
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 枚举期间文件被外部修改 / 删除：返回已读到的部分，不抛出。
            }
        }
        return list;
    }
}
