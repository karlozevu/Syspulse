using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// 读取 JSON 配置文件；文件不存在 / 整体损坏时返回默认值。
    /// 关键鲁棒性：单个字段类型错误时只回退该字段，其余设置保留——避免一个坏字段
    /// 导致主题 / 语言 / 白名单 / 阈值等全部用户配置静默丢失。
    /// </summary>
    public static T Load<T>(string path) where T : class, new()
    {
        lock (Sync)
        {
            var fallback = new T();
            try
            {
                if (!File.Exists(path)) return fallback;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return fallback;

                // 先整体验证；完全合法时走原路径（性能最好、行为不变）
                try
                {
                    return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
                }
                catch (JsonException)
                {
                    // 有字段损坏，转入逐字段合并
                }

                var incoming = JsonNode.Parse(json) as JsonObject;
                if (incoming is null) return fallback;

                // 以默认值为基底，逐字段独立验证：对每个字段单独构造"默认值对象 + 单一覆盖字段"
                // 并试反序列化，成功才采用该文件值，失败（数组内坏元素 / 非法枚举值 /
                // 类型不符）只让该字段保留默认。绝不整段接受后再统一反序列化——那样任何一个
                // 坏元素都会让全部用户配置（主题/语言/白名单/阈值）一起丢失。
                var result = JsonSerializer.SerializeToNode(fallback, Options) as JsonObject
                             ?? new JsonObject();
                var defaults = (JsonObject)(JsonSerializer.SerializeToNode(fallback, Options) ?? new JsonObject());
                foreach (var defaultKv in defaults)
                {
                    var key = defaultKv.Key;
                    // 大小写不敏感查找（Options 里启用了 PropertyNameCaseInsensitive）
                    JsonNode? value = null;
                    var found = false;
                    foreach (var kv in incoming)
                    {
                        if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        { value = kv.Value; found = true; break; }
                    }
                    if (!found || value is null) continue;

                    // 单字段试解析：把该字段替换进默认对象，看能否成功反序列化
                    var probe = (JsonObject)defaults.DeepClone();
                    probe[key] = value.DeepClone();
                    try
                    {
                        var parsed = probe.Deserialize<T>(Options);
                        if (parsed is not null) result[key] = value.DeepClone();
                    }
                    catch (JsonException)
                    {
                        // 该字段的值非法：保留默认值，不影响其它字段
                    }
                }
                return result.Deserialize<T>(Options) ?? fallback;
            }
            catch (Exception)
            {
                // 文件级错误（编码 / IO / 权限）：整体回落默认，保证程序可启动
                return fallback;
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
