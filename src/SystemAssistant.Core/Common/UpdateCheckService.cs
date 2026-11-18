using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using SystemAssistant.Core.Interfaces;
using SystemAssistant.Models.Common;

namespace SystemAssistant.Core.Common;

/// <summary>
/// 版本更新检测。默认关闭：只有在用户显式配置了 UpdateCheckUrl 时才会发出一次 GET 请求。
/// 请求内容仅为版本查询，不携带任何硬件信息、扫描结果或文件列表；也不会下载或安装更新。
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private readonly Func<string> _getUrl;

    // 静态复用：HttpClient 实现 IDisposable，逐实例创建会耗尽套接字；
    // 全局共享一个实例即可，无需让本服务再实现 IDisposable（App 层无需改动）。
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

    public UpdateCheckService(Func<string> getUrl) => _getUrl = getUrl;

    public string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "1.1.3";

    public async Task<AppVersionInfo> CheckAsync(CancellationToken token = default)
    {
        var url = _getUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new AppVersionInfo
            {
                CurrentVersion = CurrentVersion,
                Message = "未配置更新检测地址，已跳过联网检测（默认不联网）"
            };
        }

        // 必须是 https 绝对地址：校验 URL 格式并强制加密传输，避免明文 / 协议走私
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new AppVersionInfo
            {
                CurrentVersion = CurrentVersion,
                Message = "更新检测地址无效（必须为 https 绝对地址），已跳过联网检测"
            };
        }

        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                return new AppVersionInfo { CurrentVersion = CurrentVersion, Message = $"检测失败：HTTP {(int)response.StatusCode}" };
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            // 限制响应体大小：异常 / 恶意服务器返回超大 JSON 时不应耗尽内存。
            const int maxBytes = 64 * 1024;
            var buffer = new byte[maxBytes];
            var total = 0;
            int read;
            while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total), token)) > 0)
                total += read;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, total));
            var root = document.RootElement;
            // GetString() 仅在 ValueKind == String 时有效；字段是数字 / 数组 / 对象时
            // 会抛 InvalidOperationException（不在原 catch 列表内，会逃逸 CheckAsync）。
            static string Str(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
            var latest = root.TryGetProperty("version", out var v) ? Str(v) : string.Empty;
            var notes = root.TryGetProperty("notes", out var n) ? Str(n) : string.Empty;
            var page = root.TryGetProperty("url", out var u) ? Str(u) : string.Empty;

            return new AppVersionInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = latest,
                ReleaseNotes = notes,
                DownloadPage = page,
                HasUpdate = IsNewer(latest, CurrentVersion),
                Message = "检测完成"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                      or JsonException or IOException or UriFormatException
                                      or InvalidOperationException or ArgumentException)
        {
            return new AppVersionInfo { CurrentVersion = CurrentVersion, Message = "更新检测不可用：" + ex.Message };
        }
    }

    /// <summary>逐段比较数字版本号，忽略预发布后缀。</summary>
    private static bool IsNewer(string latest, string current)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        static int[] Parse(string s) => s.Split('-')[0].Split('.')
            .Select(p => int.TryParse(p, out var value) ? value : 0).ToArray();

        var a = Parse(latest);
        var b = Parse(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
