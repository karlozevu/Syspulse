using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Core.Common;
using SystemAssistant.Models.Cleaning;
using SystemAssistant.Models.Config;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.ViewModels;

/// <summary>用户设置页：主题、语言、刷新间隔、阈值、白名单、路径与更新检测。</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private int _themeIndex;
    private int _languageIndex;
    private int _refreshIntervalMs;
    private double _temperatureThreshold;
    private string _exportPath = string.Empty;
    private string _backupPath = string.Empty;
    private string _whitelistText = string.Empty;
    private string _customPathsText = string.Empty;
    private string _updateUrl = string.Empty;
    private string _updateMessage = string.Empty;

    public SettingsViewModel()
    {
        var settings = Services.Settings.Current;
        _themeIndex = (int)settings.Theme;
        _languageIndex = (int)settings.Language;
        _refreshIntervalMs = settings.ClampRefreshInterval();
        _temperatureThreshold = settings.Thresholds.FirstOrDefault(t => t.Kind == SensorKind.Temperature)?.MaxValue ?? 85;
        _exportPath = settings.DefaultExportDirectory;
        _backupPath = settings.RegistryBackupDirectory;
        _updateUrl = settings.UpdateCheckUrl;
        _whitelistText = string.Join(Environment.NewLine, settings.Whitelist.Select(r => r.Pattern));
        _customPathsText = string.Join(Environment.NewLine, settings.CustomScanPaths);

        SaveCommand = new RelayCommand(Save);
        BrowseExportCommand = CreateCommand(BrowseExportAsync);
        BrowseBackupCommand = CreateCommand(BrowseBackupAsync);
        CheckUpdateCommand = CreateCommand(CheckUpdateAsync);
    }

    public ICommand SaveCommand { get; }
    public ICommand BrowseExportCommand { get; }
    public ICommand BrowseBackupCommand { get; }
    public ICommand CheckUpdateCommand { get; }

    public int ThemeIndex { get => _themeIndex; set => Set(ref _themeIndex, Math.Clamp(value, 0, 2)); }
    public int LanguageIndex { get => _languageIndex; set => Set(ref _languageIndex, Math.Clamp(value, 0, 1)); }
    public int RefreshIntervalMs { get => _refreshIntervalMs; set => Set(ref _refreshIntervalMs, Math.Clamp(value, 500, 60_000)); }
    public double TemperatureThreshold { get => _temperatureThreshold; set => Set(ref _temperatureThreshold, Math.Clamp(value, 30, 120)); }
    public string ExportPath { get => _exportPath; set => Set(ref _exportPath, value); }
    public string BackupPath { get => _backupPath; set => Set(ref _backupPath, value); }
    public string WhitelistText { get => _whitelistText; set => Set(ref _whitelistText, value); }
    public string CustomPathsText { get => _customPathsText; set => Set(ref _customPathsText, value); }
    public string UpdateUrl { get => _updateUrl; set => Set(ref _updateUrl, value); }
    public string UpdateMessage { get => _updateMessage; private set => Set(ref _updateMessage, value); }

    private void Save()
    {
        var theme = (ThemeMode)ThemeIndex;
        var language = (LanguageMode)LanguageIndex;
        var whitelist = ParseLines(WhitelistText).Select(pattern => new WhitelistRule
        {
            Pattern = pattern,
            Kind = pattern.StartsWith('.') ? "Extension" : Path.HasExtension(pattern) ? "FileName" : "Directory",
            Enabled = true
        }).ToArray();

        Services.Settings.Update(s =>
        {
            s.Theme = theme;
            s.Language = language;
            s.RefreshIntervalMs = RefreshIntervalMs;
            s.DefaultExportDirectory = ExportPath;
            s.RegistryBackupDirectory = BackupPath;
            s.UpdateCheckUrl = UpdateUrl.Trim();
            s.Whitelist = whitelist;
            s.CustomScanPaths = ParseLines(CustomPathsText);
            s.Thresholds = new[]
            {
                // 全局温度阈值：SensorIdentifier 为空表示对所有温度传感器生效
                new SensorThreshold { Kind = SensorKind.Temperature, MaxValue = TemperatureThreshold, Enabled = true }
            };
        });

        Services.ApplyRuntimeSettings();
        ThemeService.Apply(theme);
        LocalizationService.Instance.SetLanguage(language);
        Services.OperationLog.Log(Models.Common.OperationType.Settings, "保存用户设置",
            $"{L("Settings_Interval")}={RefreshIntervalMs}；白名单 {whitelist.Length} 条", true);
        StatusMessage = L("Settings_Saved");
    }

    private async Task BrowseExportAsync()
    {
        var folder = await DialogService.PickFolderAsync(L("Settings_ExportPath"));
        if (!string.IsNullOrWhiteSpace(folder)) ExportPath = folder;
    }

    private async Task BrowseBackupAsync()
    {
        var folder = await DialogService.PickFolderAsync(L("Settings_BackupPath"));
        if (!string.IsNullOrWhiteSpace(folder)) BackupPath = folder;
    }

    private async Task CheckUpdateAsync()
    {
        // 若 URL 为空，UpdateCheckService 不会发出任何网络请求
        var urlChanged = !string.Equals(Services.Settings.Current.UpdateCheckUrl, UpdateUrl.Trim(), StringComparison.Ordinal);
        if (urlChanged) Services.Settings.Update(s => s.UpdateCheckUrl = UpdateUrl.Trim());
        var info = await Services.Update.CheckAsync();
        UpdateMessage = info.HasUpdate
            ? LF("Update_Available", info.LatestVersion, info.CurrentVersion) + "  " + info.ReleaseNotes
            : info.Message;
        StatusMessage = UpdateMessage;
    }

    private static IReadOnlyList<string> ParseLines(string text)
        => (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
