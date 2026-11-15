using System.Globalization;
using System.Management;

namespace SystemAssistant.Core.Hardware;

/// <summary>
/// WMI 查询的容错封装。所有查询都在 try/catch 内执行：
/// 某个 WMI 提供程序不可用（常见于精简版系统或权限不足）时返回空集合，绝不让硬件页面崩溃。
/// </summary>
internal static class WmiQuery
{
    public const string CimV2 = @"root\CIMV2";
    public const string WmiRoot = @"root\WMI";
    public const string Storage = @"root\Microsoft\Windows\Storage";

    /// <summary>执行 SELECT 查询并把结果转成「属性名 → 值」字典，自动释放 WMI 对象。</summary>
    public static IReadOnlyList<WmiObject> Select(string scope, string className, string? condition = null)
    {
        var result = new List<WmiObject>();
        var query = $"SELECT * FROM {className}" + (string.IsNullOrWhiteSpace(condition) ? "" : $" WHERE {condition}");
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in mo.Properties)
                    {
                        try { values[property.Name] = property.Value; }
                        catch (ManagementException) { values[property.Name] = null; }
                    }
                    result.Add(new WmiObject(values));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            // 该类不可用：返回已取到的部分结果
        }
        return result;
    }

    /// <summary>获取单个 WMI 类的第一条记录。</summary>
    public static WmiObject? First(string scope, string className, string? condition = null)
        => Select(scope, className, condition).FirstOrDefault();

    /// <summary>遍历活动 WMI 对象（用于 GetRelated 关联查询）。回调期间对象有效。</summary>
    public static void ForEach(string scope, string className, Action<ManagementObject> action, string? condition = null)
    {
        var query = $"SELECT * FROM {className}" + (string.IsNullOrWhiteSpace(condition) ? "" : $" WHERE {condition}");
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    try { action(mo); }
                    catch (Exception)
                    {
                        // 回调里的转换 / 格式化异常（例如 BuildDisks 的 Convert.ToInt32）
                        // 必须就地消化，否则会让整个硬件查询任务 faulted。
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            // 忽略不可用的 WMI 类
        }
    }
}

/// <summary>一次 WMI 记录的类型安全读取包装。</summary>
internal sealed class WmiObject
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    public WmiObject(IReadOnlyDictionary<string, object?> values) => _values = values;

    public string String(params string[] names)
    {
        foreach (var name in names)
        {
            if (!_values.TryGetValue(name, out var value) || value is null) continue;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrEmpty(text)) return text!;
        }
        return string.Empty;
    }

    public long Int64(params string[] names)
    {
        foreach (var name in names)
        {
            if (!_values.TryGetValue(name, out var value) || value is null) continue;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { }
        }
        return 0;
    }

    public int Int32(params string[] names) => (int)Math.Clamp(Int64(names), int.MinValue, int.MaxValue);

    public double Double(params string[] names)
    {
        foreach (var name in names)
        {
            if (!_values.TryGetValue(name, out var value) || value is null) continue;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { }
        }
        return 0;
    }

    public bool Bool(params string[] names)
    {
        foreach (var name in names)
        {
            if (!_values.TryGetValue(name, out var value) || value is null) continue;
            if (value is bool b) return b;
            if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var number)) return number != 0;
        }
        return false;
    }

    public byte[]? Bytes(string name)
    {
        if (!_values.TryGetValue(name, out var value)) return null;
        return value as byte[] ?? (value is ushort[] shorts ? shorts.Select(s => (byte)s).ToArray() : null);
    }

    public string[] StringArray(string name)
    {
        if (!_values.TryGetValue(name, out var value) || value is null) return Array.Empty<string>();
        return value is string[] array ? array : new[] { Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    }
}
