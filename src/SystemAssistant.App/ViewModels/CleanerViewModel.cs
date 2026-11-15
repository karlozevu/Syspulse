using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Models.Cleaning;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.ViewModels;

/// <summary>
/// 安全清理页 ViewModel。扫描只读，删除前必须经过「明细确认 + 高危二次确认」，
/// 且注册表项目必须先完成注册表备份。
/// </summary>
public sealed class CleanerViewModel : ViewModelBase
{
    private IReadOnlyList<ScanGroup> _groups = Array.Empty<ScanGroup>();
    private bool _scanSystemJunk = true;
    private bool _scanBrowserCache = true;
    private bool _scanBrowserHistory;
    private bool _scanBrowserCookies;
    private bool _scanBrowserDownloads;
    private bool _scanApplicationCache = true;
    private bool _scanRegistryResidue;
    private string _customPathsText = string.Empty;
    private string _whitelistText = string.Empty;
    private string _summary = string.Empty;
    private double _progressPercent;
    private string _progressText = string.Empty;
    private string _warningText = string.Empty;
    private bool _hasResult;

    public CleanerViewModel()
    {
        var settings = Services.Settings.Current;
        _customPathsText = string.Join(Environment.NewLine, settings.CustomScanPaths);
        _whitelistText = string.Join(Environment.NewLine, settings.Whitelist.Select(r => r.Pattern));

        ScanCommand = CreateCommand(ScanAsync, () => !IsBusy);
        CleanCommand = CreateCommand(CleanAsync, () => !IsBusy && HasResult);
        BackupRegistryCommand = CreateCommand(BackupRegistryAsync, () => !IsBusy);
        AddCustomPathCommand = CreateCommand(AddCustomPathAsync);
        SaveWhitelistCommand = new RelayCommand(SaveWhitelist);
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
    }

    public event Action? GroupsChanged;

    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand BackupRegistryCommand { get; }
    public ICommand AddCustomPathCommand { get; }
    public ICommand SaveWhitelistCommand { get; }
    public ICommand SelectNoneCommand { get; }

    public IReadOnlyList<ScanGroup> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public bool ScanSystemJunk { get => _scanSystemJunk; set => Set(ref _scanSystemJunk, value); }
    public bool ScanBrowserCache { get => _scanBrowserCache; set => Set(ref _scanBrowserCache, value); }
    public bool ScanBrowserHistory { get => _scanBrowserHistory; set => Set(ref _scanBrowserHistory, value); }
    public bool ScanBrowserCookies { get => _scanBrowserCookies; set => Set(ref _scanBrowserCookies, value); }
    public bool ScanBrowserDownloads { get => _scanBrowserDownloads; set => Set(ref _scanBrowserDownloads, value); }
    public bool ScanApplicationCache { get => _scanApplicationCache; set => Set(ref _scanApplicationCache, value); }
    public bool ScanRegistryResidue { get => _scanRegistryResidue; set => Set(ref _scanRegistryResidue, value); }
    public string CustomPathsText { get => _customPathsText; set => Set(ref _customPathsText, value); }
    public string WhitelistText { get => _whitelistText; set => Set(ref _whitelistText, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string WarningText { get => _warningText; private set => Set(ref _warningText, value); }
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public bool HasResult { get => _hasResult; private set => Set(ref _hasResult, value); }

    public bool RegistryBackupDone => Services.CleanService.RegistryBackupDone;

    private ScanOptions BuildOptions() => new()
    {
        ScanSystemJunk = ScanSystemJunk,
        ScanBrowserCache = ScanBrowserCache,
        ScanBrowserHistory = ScanBrowserHistory,
        ScanBrowserCookies = ScanBrowserCookies,
        ScanBrowserDownloads = ScanBrowserDownloads,
        ScanApplicationCache = ScanApplicationCache,
        ScanRegistryResidue = ScanRegistryResidue,
        CustomPaths = ParseLines(CustomPathsText)
    };

    private async Task ScanAsync()
    {
        await RunBusyAsync(async () =>
        {
            ProgressPercent = 0;
            var progress = new Progress<string>(text => ProgressText = text);
            var result = await Services.Clean.ScanAsync(BuildOptions(), progress);
            Groups = result.Groups;
            HasResult = true;
            Summary = LF("Cleaner_Total", result.TotalItemCount, FormatHelper.FormatBytes(result.TotalSizeBytes))
                      + " · " + LF("Cleaner_Skipped", result.SkippedByWhitelistCount);
            WarningText = string.Join(Environment.NewLine, result.Warnings);
            ProgressPercent = 100;
            ProgressText = L("Status_Done");
            GroupsChanged?.Invoke();
        }, L("Common_Loading"));
    }

    private async Task CleanAsync()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = L("Status_NoSelection");
            return;
        }

        // 注册表项：必须先在本次会话内完成备份，否则中止
        if (selected.Any(i => i.IsRegistryEntry) && !Services.CleanService.RegistryBackupDone)
        {
            var proceed = await DialogService.ConfirmAsync(L("Cleaner_ConfirmTitle"),
                L("Cleaner_BackupRequired") + " " + L("Cleaner_BackupHint"));
            if (!proceed) return;
            await BackupRegistryAsync();
            if (!Services.CleanService.RegistryBackupDone)
            {
                StatusMessage = L("Common_AdminRequired");
                return;
            }
        }

        var totalSize = selected.Sum(i => i.SizeBytes);
        var highRisk = selected.Count(i => i.Risk > RiskLevel.Low);
        var detail = string.Join(Environment.NewLine, selected.Take(40).Select(i =>
            $"  [{(i.Risk == RiskLevel.High ? "高" : i.Risk == RiskLevel.Medium ? "中" : "低")}] {i.Path}"))
            + (selected.Length > 40 ? Environment.NewLine + $"  ... 其余 {selected.Length - 40} 项" : string.Empty);

        var first = await DialogService.ConfirmAsync(L("Cleaner_ConfirmTitle"),
            LF("Cleaner_ConfirmBody", selected.Length, FormatHelper.FormatBytes(totalSize)), detail);
        if (!first) return;

        if (highRisk > 0)
        {
            var second = await new Views.Dialogs.ConfirmDialog(L("Cleaner_SecondTitle"),
                LF("Cleaner_SecondBody", highRisk), detail,
                requireAcknowledge: true,
                acknowledgeText: L("Cleaner_Acknowledge")).ShowDialog<bool>(ThemeService.GetMainWindow()
                    ?? throw new InvalidOperationException("主窗口尚未创建"));
            if (!second) return;
        }

        await RunBusyAsync(async () =>
        {
            var progress = new Progress<CleanProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressText = $"{p.Processed}/{p.Total}  {p.CurrentItem}";
            });
            var result = await Services.Clean.CleanAsync(selected, highRiskConfirmed: true, progress);
            StatusMessage = LF("Cleaner_Result", result.SucceededCount, result.FailedCount,
                FormatHelper.FormatBytes(result.ReclaimedBytes));
            if (result.Errors.Count > 0)
            {
                WarningText = string.Join(Environment.NewLine, result.Errors.Take(20));
            }
            Groups = Array.Empty<ScanGroup>();
            HasResult = false;
            GroupsChanged?.Invoke();
        }, L("Common_Clean"));
    }

    private async Task BackupRegistryAsync()
    {
        var directory = await DialogService.PickFolderAsync(L("Cleaner_Backup"));
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Services.Settings.Current.RegistryBackupDirectory;
            if (string.IsNullOrWhiteSpace(directory)) return;
        }
        await RunBusyAsync(async () =>
        {
            var result = await Services.Clean.BackupRegistryAsync(directory);
            StatusMessage = result.Success
                ? L("Cleaner_BackupHint") + " → " + result.ManifestPath
                : string.Join("；", result.Errors);
            Raise(nameof(RegistryBackupDone));
            if (!result.Success) await DialogService.ErrorAsync(StatusMessage);
        }, L("Cleaner_Backup"));
    }

    private async Task AddCustomPathAsync()
    {
        var folder = await DialogService.PickFolderAsync(L("Cleaner_CustomPaths"));
        if (string.IsNullOrWhiteSpace(folder)) return;
        var lines = ParseLines(CustomPathsText).ToList();
        if (!lines.Contains(folder, StringComparer.OrdinalIgnoreCase)) lines.Add(folder);
        CustomPathsText = string.Join(Environment.NewLine, lines);
        SaveSettings();
    }

    private void SaveWhitelist()
    {
        var rules = ParseLines(WhitelistText).Select(pattern => new WhitelistRule
        {
            Pattern = pattern,
            Kind = pattern.StartsWith('.') ? "Extension" : Path.HasExtension(pattern) ? "FileName" : "Directory",
            Enabled = true
        }).ToArray();
        Services.Settings.Update(s => s.Whitelist = rules);
        StatusMessage = L("Settings_Saved");
    }

    private void SaveSettings()
    {
        Services.Settings.Update(s => s.CustomScanPaths = ParseLines(CustomPathsText));
        StatusMessage = L("Settings_Saved");
    }

    private void SetAll(bool value)
    {
        foreach (var item in Groups.SelectMany(g => g.Items)) item.IsChecked = value;
        GroupsChanged?.Invoke();
    }

    private static IReadOnlyList<string> ParseLines(string text)
        => (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
