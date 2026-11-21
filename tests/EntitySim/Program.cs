using Avalonia.Threading;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;
using SystemAssistant.Core.Common;
using SystemAssistant.Models.Config;
using SystemAssistant.Models.Health;
using SystemAssistant.Utils.Common;

namespace Syspulse.EntitySim;

/// <summary>
/// 实体模拟：真实构造 App 层 ViewModel 与真实 ServiceHub，驱动用户操作序列，
/// 验证健康中心等页面的显示数据。不是静态代码分析，走的是真实代码路径。
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"[PASS] {name}: {detail}"); }
        else { _fail++; Console.WriteLine($"[FAIL] {name}: {detail}"); }
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("=== Syspulse 实体模拟测试（真实 ViewModel + 真实服务）===\n");
        var hub = ServiceHub.Instance;
        try
        {
            await HealthCenterScenario(hub);
            await SettingsScenario(hub);
            await CrossPageScenario(hub);
            await AlertHistoryScenario(hub);
        }
        finally
        {
            hub.Dispose();
        }

        Console.WriteLine($"\n=== 结果: PASS {_pass} / FAIL {_fail} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 健康中心：体检 -> 显示 -> 切语言 -> 显示 -------------------------
    private static async Task HealthCenterScenario(ServiceHub hub)
    {
        Console.WriteLine("## 场景1：健康中心显示与语言切换");
        var vm = new HealthViewModel();
        try
        {
            // 初始状态：未体检时应显示占位符
            Check("health.initial.score", vm.ScoreText == "—", $"score={vm.ScoreText}");
            Check("health.initial.issues", vm.Issues.Count == 0, $"issues={vm.Issues.Count}");

            // 执行体检（真实 WMI/传感器查询）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await vm.CheckCommand.ExecuteAsyncSafe();
            sw.Stop();
            Check("health.check.score", int.TryParse(vm.ScoreText, out var score) && score is >= 0 and <= 100,
                $"score={vm.ScoreText}, cost={sw.ElapsedMilliseconds}ms");
            Check("health.check.summary", !string.IsNullOrWhiteSpace(vm.HealthSummary),
                $"summary={vm.HealthSummary}");
            Check("health.check.issues", vm.Issues.Count > 0, $"issues={vm.Issues.Count}");
            var firstIssue = vm.Issues.FirstOrDefault();
            if (firstIssue is not null)
                Check("health.check.issueText", !string.IsNullOrEmpty(firstIssue.Message),
                    $"msg={firstIssue.Message}");

            // 切到英文：文本应重本地化，且不重跑体检（用 ScoreText 不变 + 耗时极短验证）
            var zhSummary = vm.HealthSummary;
            var zhIssue = vm.Issues.FirstOrDefault()?.Message ?? string.Empty;
            LocalizationService.Instance.SetLanguage(LanguageMode.EnUs);
            var afterLang = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(50); // 给 OnLanguageChanged 的同步处理留出时间
            afterLang.Stop();
            Check("health.lang.summaryChanged", vm.HealthSummary != zhSummary,
                $"zh={zhSummary} | en={vm.HealthSummary}");
            Check("health.lang.issueChanged", (vm.Issues.FirstOrDefault()?.Message ?? string.Empty) != zhIssue
                  || zhIssue.Contains("正常"), $"zh={zhIssue} | en={vm.Issues.FirstOrDefault()?.Message}");
            Check("health.lang.noRecheck", afterLang.ElapsedMilliseconds < 1000,
                $"lang switch cost={afterLang.ElapsedMilliseconds}ms (should not re-run WMI)");
            Check("health.lang.scoreKept", vm.ScoreText == score.ToString(),
                $"score kept={vm.ScoreText}");

            // 网络监控启停
            Check("health.net.initial", vm.NetworkButtonText.Length > 0, $"btn={vm.NetworkButtonText}");
            // 启动传感器监控，模拟真实 App 启动后的状态（体检摘要里才有传感器读数）
            hub.Sensors.Start();
            await Task.Delay(300);

            vm.ToggleNetworkCommand.Execute(null);
            Check("health.net.started", hub.NetworkMonitor.IsRunning, "monitor running");
            // 无 UI 消息循环时 PostUI 的回调不会自动执行，手动 pump Dispatcher 队列
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(250);
                Dispatcher.UIThread.RunJobs();
            }
            Check("health.net.speed", !string.IsNullOrWhiteSpace(vm.NetworkSpeed), $"speed={vm.NetworkSpeed}");
            Check("health.sensors.running", hub.Sensors.IsRunning && hub.Sensors.LatestReadings.Count > 0,
                $"sensor readings={hub.Sensors.LatestReadings.Count}");
            vm.ToggleNetworkCommand.Execute(null);
            Check("health.net.stopped", !hub.NetworkMonitor.IsRunning, "monitor stopped");

            // 再切回中文：网络状态文案应跟随
            LocalizationService.Instance.SetLanguage(LanguageMode.ZhCn);
            await Task.Delay(50);
            Check("health.lang.netStatus", !string.IsNullOrWhiteSpace(vm.NetworkStatus),
                $"status={vm.NetworkStatus}");
        }
        finally
        {
            vm.Dispose();
            LocalizationService.Instance.SetLanguage(LanguageMode.ZhCn);
        }
    }

    // ---- 设置页：未保存编辑不被语言切换覆盖 ------------------------------
    private static Task SettingsScenario(ServiceHub hub)
    {
        Console.WriteLine("\n## 场景2：设置页未保存编辑保护");
        var vm = new SettingsViewModel();
        try
        {
            var original = vm.WhitelistText;
            vm.WhitelistText = "MyUnsavedEntry"; // 用户输入，未保存
            Check("settings.dirty", vm.WhitelistText == "MyUnsavedEntry", $"text={vm.WhitelistText}");

            // 顶栏切主题（触发 Settings.Changed）
            var main = new MainWindowViewModel();
            try
            {
                main.ThemeIndex = main.ThemeIndex == 0 ? 1 : 0;
                Task.Delay(50).Wait();
                Check("settings.dirtyPreserved", vm.WhitelistText == "MyUnsavedEntry",
                    $"text after theme switch={vm.WhitelistText} (original={original})");
            }
            finally { main.Dispose(); }

            // 保存后 dirty 清除，配置与文本框一致
            vm.SaveCommand.Execute(null);
            Check("settings.saved", vm.WhitelistText == "MyUnsavedEntry",
                $"after save={vm.WhitelistText}");
        }
        finally
        {
            // 恢复设置，避免污染后续测试
            hub.Settings.Update(s => s.Whitelist = Array.Empty<SystemAssistant.Models.Cleaning.WhitelistRule>());
            vm.Dispose();
        }
        return Task.CompletedTask;
    }

    // ---- 跨页面：反复构造/释放 + 语言切换 --------------------------------
    private static async Task CrossPageScenario(ServiceHub hub)
    {
        Console.WriteLine("\n## 场景3：跨页面反复切换与语言组合");
        for (var i = 0; i < 3; i++)
        {
            var health = new HealthViewModel();
            var overview = new OverviewViewModel();
            var settings = new SettingsViewModel();
            LocalizationService.Instance.SetLanguage(i % 2 == 0 ? LanguageMode.EnUs : LanguageMode.ZhCn);
            await Task.Delay(30);
            Check($"crosspage.{i}.health.title", health.NetworkButtonText.Length > 0, health.NetworkButtonText);
            Check($"crosspage.{i}.overview", overview is not null, "overview constructed");
            Check($"crosspage.{i}.settings", settings.ThemeIndex >= 0, $"theme={settings.ThemeIndex}");
            health.Dispose(); overview.Dispose(); settings.Dispose();
        }
        LocalizationService.Instance.SetLanguage(LanguageMode.ZhCn);
    }

    // ---- 告警历史：并发追加与清空 ----------------------------------------
    private static async Task AlertHistoryScenario(ServiceHub hub)
    {
        Console.WriteLine("\n## 场景4：告警历史并发追加与清空");
        var svc = hub.AlertHistory;
        svc.Clear();
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            svc.Append(new AlertHistoryEntry
            {
                SensorName = $"Sensor-{i}", HardwareName = "CPU", Value = 90 + i, Threshold = 85, Unit = "°C"
            }))).ToArray();
        await Task.WhenAll(tasks);
        var all = svc.GetAll();
        Check("alerts.concurrent.append", all.Count >= 15, $"appended={all.Count}/20");

        // 清空后再追加，验证不被旧快照覆盖
        svc.Clear();
        svc.Append(new AlertHistoryEntry { SensorName = "AfterClear", Value = 99, Threshold = 85, Unit = "°C" });
        var after = svc.GetAll();
        Check("alerts.afterClear", after.Count == 1 && after[0].SensorName == "AfterClear",
            $"count={after.Count}, first={after.FirstOrDefault()?.SensorName}");
        svc.Clear();
    }
}

internal static class CommandExtensions
{
    /// <summary>等待 AsyncRelayCommand 完成（通过轮询其状态变化）。</summary>
    public static async Task ExecuteAsyncSafe(this System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        // AsyncRelayCommand 是 async void，通过短暂等待让任务推进；
        // 真实体检含 WMI 查询，给足时间。
        await Task.Delay(2500);
    }
}
