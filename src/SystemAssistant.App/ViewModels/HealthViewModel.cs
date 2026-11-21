using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.Models.Health;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.ViewModels;

/// <summary>健康中心页：系统体检、硬件告警历史、实时网络监控三合一。</summary>
public sealed class HealthViewModel : ViewModelBase
{
    private string _scoreText = "—";
    private string _scoreDetail = string.Empty;
    private string _healthSummary = string.Empty;
    private bool _isChecking;
    private string _networkStatus = string.Empty;
    private string _networkStatusKey = string.Empty; // 缓存状态 key，切语言时据此重建文案
    private string _networkSpeed = string.Empty;
    private IReadOnlyList<NetworkSample> _samples = Array.Empty<NetworkSample>();
    // 缓存原始报告与告警，切语言时只重新本地化文本，不重跑昂贵的体检 / 不重复读盘。
    private HealthReport? _lastReport;
    private readonly List<AlertHistoryEntry> _lastAlerts = new();
    private bool _allGoodPlaceholder;

    public HealthViewModel()
    {
        CheckCommand = CreateCommand(CheckAsync);
        ClearAlertsCommand = CreateSyncCommand(ClearAlerts);
        ToggleNetworkCommand = CreateSyncCommand(ToggleNetwork);
        RefreshAlertsCommand = CreateSyncCommand(RefreshAlerts);

        Services.NetworkMonitor.SampleUpdated += OnNetworkSample;
        RefreshAlerts();
    }

    public ICommand CheckCommand { get; }
    public ICommand ClearAlertsCommand { get; }
    public ICommand ToggleNetworkCommand { get; }
    public ICommand RefreshAlertsCommand { get; }

    /// <summary>告警列表变化（View 订阅后重新填充表格）。</summary>
    public event Action? RefreshAlertsRequested;

    public ObservableCollection<HealthIssueVm> Issues { get; } = new();
    public ObservableCollection<AlertHistoryVm> Alerts { get; } = new();

    public string ScoreText { get => _scoreText; private set => Set(ref _scoreText, value); }
    public string ScoreDetail { get => _scoreDetail; private set => Set(ref _scoreDetail, value); }
    public string HealthSummary { get => _healthSummary; private set => Set(ref _healthSummary, value); }
    public bool IsChecking { get => _isChecking; private set => Set(ref _isChecking, value); }
    public string NetworkStatus { get => _networkStatus; private set => Set(ref _networkStatus, value); }
    public string NetworkSpeed { get => _networkSpeed; private set => Set(ref _networkSpeed, value); }
    public string NetworkButtonKey => Services.NetworkMonitor.IsRunning
        ? "Health_NetworkStop" : "Health_NetworkStart";

    public string NetworkButtonText => L(NetworkButtonKey);

    public IReadOnlyList<NetworkSample> Samples => _samples;

    /// <summary>网络速率曲线更新事件（UI 层绘制）。</summary>
    public event Action<IReadOnlyList<NetworkSample>>? NetworkSamplesUpdated;

    private async Task CheckAsync()
    {
        await RunBusyAsync(async () =>
        {
            IsChecking = true;
            try
            {
                var report = await Services.HealthCheck.RunAsync();
                _lastReport = report;
                ScoreText = report.Score.ToString(CultureInfo.InvariantCulture);
                ScoreDetail = ScoreLabel(report.Score);
                RefreshHealthText();

                Issues.Clear();
                _allGoodPlaceholder = report.Issues.Count == 0;
                if (_allGoodPlaceholder)
                {
                    Issues.Add(new HealthIssueVm { SeverityKey = "Health_AllGood", Category = string.Empty, MessageKey = "Health_NoIssues" });
                }
                else
                {
                    foreach (var issue in report.Issues)
                    {
                        Issues.Add(new HealthIssueVm
                        {
                            SeverityKey = SeverityKey(issue.Severity),
                            Category = issue.Category,
                            MessageKey = issue.MessageKey,
                            MessageArg = issue.MessageArg,
                            IsWarning = issue.Severity != HealthSeverity.Info
                        });
                    }
                }
                ApplyIssueText();
                StatusMessage = L("Status_Done");
            }
            finally { IsChecking = false; }
        }, L("Health_Check"));
    }

    /// <summary>用当前报告重建顶部摘要（体检数值本身不变，只换本地化文案）。</summary>
    private void RefreshHealthText()
    {
        var report = _lastReport;
        if (report is null) return;
        var parts = new List<string>
        {
            LF("Health_CpuUsage", report.CpuUsagePercent.ToString("F1", CultureInfo.InvariantCulture)),
            LF("Health_MemUsage", report.MemoryUsagePercent.ToString("F1", CultureInfo.InvariantCulture))
        };
        if (report.CpuTemperature is { } t)
            parts.Add(LF("Health_CpuTemp", t.ToString("F0", CultureInfo.InvariantCulture)));
        parts.Add(LF("Health_Sensors", report.SensorCount.ToString(CultureInfo.InvariantCulture)));
        HealthSummary = string.Join("  ·  ", parts);
        ScoreDetail = ScoreLabel(report.Score);
    }

    private void RefreshAlerts()
    {
        // 只在真正需要刷新时读盘：切语言不应重复读取（避免额外的 IO 与状态抖动）。
        _lastAlerts.Clear();
        _lastAlerts.AddRange(Services.AlertHistory.GetAll());
        ApplyAlertText();
    }

    /// <summary>用缓存的原始告警重建展示文本（不读盘）。</summary>
    private void ApplyAlertText()
    {
        Alerts.Clear();
        foreach (var a in _lastAlerts.AsEnumerable().Reverse().Take(100))
        {
            Alerts.Add(new AlertHistoryVm
            {
                Time = a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Sensor = a.SensorName,
                Message = LF("Health_AlertDetail", a.Value.ToString("F1", CultureInfo.InvariantCulture),
                    a.Unit, a.Threshold.ToString("F1", CultureInfo.InvariantCulture))
            });
        }
        RefreshAlertsRequested?.Invoke();
    }

    /// <summary>就地更新已有问题项文案（保留集合对象，ItemsControl 不需重建）。</summary>
    private void ApplyIssueText()
    {
        foreach (var vm in Issues)
        {
            vm.SeverityText = vm.SeverityKey == "Health_AllGood" ? L(vm.SeverityKey) : SeverityGlyph(vm.SeverityKey);
            vm.Message = string.IsNullOrEmpty(vm.MessageArg) ? L(vm.MessageKey) : LF(vm.MessageKey, vm.MessageArg);
        }
    }

    private void ClearAlerts()
    {
        Services.AlertHistory.Clear();
        _lastAlerts.Clear();
        Alerts.Clear();
        RefreshAlertsRequested?.Invoke();
        StatusMessage = L("Status_Done");
    }

    private void ToggleNetwork()
    {
        if (Services.NetworkMonitor.IsRunning)
        {
            Services.NetworkMonitor.Stop();
            SetNetworkStatus("Health_NetworkStopped");
            NetworkSpeed = string.Empty;
            _samples = Array.Empty<NetworkSample>();
        }
        else
        {
            Services.NetworkMonitor.Start();
            SetNetworkStatus("Health_NetworkRunning");
        }
        Raise(nameof(NetworkButtonKey));
        Raise(nameof(NetworkButtonText));
        NetworkSamplesUpdated?.Invoke(_samples);
    }

    private void SetNetworkStatus(string key)
    {
        _networkStatusKey = key;
        NetworkStatus = L(key);
    }

    private void OnNetworkSample(IReadOnlyList<NetworkSample> samples)
    {
        PostUI(() =>
        {
            _samples = samples;
            var latest = samples.LastOrDefault();
            if (latest is not null)
                NetworkSpeed = LF("Health_NetworkSpeed",
                    FormatHelper.FormatSpeed(latest.DownloadBytesPerSecond),
                    FormatHelper.FormatSpeed(latest.UploadBytesPerSecond));
            NetworkSamplesUpdated?.Invoke(_samples);
        });
    }

    private string ScoreLabel(int score) => score switch
    {
        >= 90 => L("Health_ScoreExcellent"),
        >= 75 => L("Health_ScoreGood"),
        >= 55 => L("Health_ScoreFair"),
        _ => L("Health_ScorePoor")
    };

    private static string SeverityKey(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => "Critical",
        HealthSeverity.Warning => "Warning",
        _ => "Info"
    };

    private static string SeverityGlyph(string key) => key switch
    {
        "Critical" => "●",
        "Warning" => "▲",
        _ => "○"
    };

    protected override void OnLanguageChanged()
    {
        // 只重本地化已缓存的数据，不重跑体检、不重复读盘、不重建网络采样。
        ApplyIssueText();
        RefreshHealthText();
        ApplyAlertText();
        Raise(nameof(NetworkButtonText));
        if (!string.IsNullOrEmpty(_networkStatusKey))
            NetworkStatus = L(_networkStatusKey);
        if (_allGoodPlaceholder && Issues.Count > 0)
            Issues[0].SeverityText = L("Health_AllGood");
    }

    public override void Dispose()
    {
        Services.NetworkMonitor.SampleUpdated -= OnNetworkSample;
        if (Services.NetworkMonitor.IsRunning) Services.NetworkMonitor.Stop();
        base.Dispose();
    }
}

/// <summary>体检问题展示项：文案可就地更新（切语言时不需重建集合）。</summary>
public sealed class HealthIssueVm : ObservableObject
{
    private string _severityText = string.Empty;
    private string _message = string.Empty;

    public required string SeverityKey { get; init; }
    public required string Category { get; init; }
    public required string MessageKey { get; init; }
    public string MessageArg { get; init; } = string.Empty;
    public bool IsWarning { get; init; }

    public string SeverityText { get => _severityText; set => Set(ref _severityText, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
}

/// <summary>告警历史展示项：文本一次性生成即可，切语言时由 VM 重建集合。</summary>
public sealed class AlertHistoryVm
{
    public required string Time { get; init; }
    public required string Sensor { get; init; }
    public required string Message { get; init; }
}
