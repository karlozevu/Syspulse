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
    private string _networkSpeed = string.Empty;
    private IReadOnlyList<NetworkSample> _samples = Array.Empty<NetworkSample>();

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
                ScoreText = report.Score.ToString(CultureInfo.InvariantCulture);
                ScoreDetail = ScoreLabel(report.Score);
                var parts = new List<string>
                {
                    LF("Health_CpuUsage", report.CpuUsagePercent.ToString("F1", CultureInfo.InvariantCulture)),
                    LF("Health_MemUsage", report.MemoryUsagePercent.ToString("F1", CultureInfo.InvariantCulture))
                };
                if (report.CpuTemperature is { } t)
                    parts.Add(LF("Health_CpuTemp", t.ToString("F0", CultureInfo.InvariantCulture)));
                parts.Add(LF("Health_Sensors", report.SensorCount.ToString(CultureInfo.InvariantCulture)));
                HealthSummary = string.Join("  ·  ", parts);

                Issues.Clear();
                foreach (var issue in report.Issues)
                {
                    issue.Message = string.IsNullOrEmpty(issue.MessageArg)
                        ? L(issue.MessageKey)
                        : LF(issue.MessageKey, issue.MessageArg);
                    Issues.Add(new HealthIssueVm
                    {
                        SeverityText = SeverityLabel(issue.Severity),
                        Category = issue.Category,
                        Message = issue.Message,
                        IsWarning = issue.Severity != HealthSeverity.Info
                    });
                }
                if (Issues.Count == 0)
                    Issues.Add(new HealthIssueVm
                    {
                        SeverityText = L("Health_AllGood"),
                        Category = string.Empty,
                        Message = L("Health_NoIssues"),
                        IsWarning = false
                    });
                StatusMessage = L("Status_Done");
            }
            finally { IsChecking = false; }
        }, L("Health_Check"));
    }

    private void RefreshAlerts()
    {
        Alerts.Clear();
        foreach (var a in Services.AlertHistory.GetAll().AsEnumerable().Reverse().Take(100))
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

    private void ClearAlerts()
    {
        Services.AlertHistory.Clear();
        Alerts.Clear();
        RefreshAlertsRequested?.Invoke();
        StatusMessage = L("Status_Done");
    }

    private void ToggleNetwork()
    {
        if (Services.NetworkMonitor.IsRunning)
        {
            Services.NetworkMonitor.Stop();
            NetworkStatus = L("Health_NetworkStopped");
            NetworkSpeed = string.Empty;
            _samples = Array.Empty<NetworkSample>();
        }
        else
        {
            Services.NetworkMonitor.Start();
            NetworkStatus = L("Health_NetworkRunning");
        }
        Raise(nameof(NetworkButtonKey));
        Raise(nameof(NetworkButtonText));
        NetworkSamplesUpdated?.Invoke(_samples);
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

    private static string SeverityLabel(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => "●",
        HealthSeverity.Warning => "▲",
        _ => "○"
    };

    protected override void OnLanguageChanged()
    {
        RefreshAlerts();
        Raise(nameof(NetworkButtonText));
        // 语言切换后若有体检结果，重新渲染消息（含评分档位文案）
        if (Issues.Count > 0 && !IsChecking) _ = CheckAsync();
    }

    public override void Dispose()
    {
        Services.NetworkMonitor.SampleUpdated -= OnNetworkSample;
        if (Services.NetworkMonitor.IsRunning) Services.NetworkMonitor.Stop();
        base.Dispose();
    }
}

public sealed class HealthIssueVm
{
    public required string SeverityText { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public bool IsWarning { get; init; }
}

public sealed class AlertHistoryVm
{
    public required string Time { get; init; }
    public required string Sensor { get; init; }
    public required string Message { get; init; }
}
