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
    private SystemAssistant.Models.Common.AppVersionInfo? _lastUpdateInfo;

    public SettingsViewModel()
    {
        var settings = Services.Settings.Current;
        _themeIndex = (int)settings.Theme;
        _languageIndex = (int)settings.Language;
        _refreshIntervalMs = settings.ClampRefreshInterval();
        _temperatureThreshold = settings.Thresholds.FirstOrDefault(t => t.Kind == SensorKind.Temperature)?.MaxValue ?? 85;
        ReloadTextFields();

        SaveCommand = CreateSyncCommand(Save);
        BrowseExportCommand = CreateCommand(BrowseExportAsync);
        BrowseBackupCommand = CreateCommand(BrowseBackupAsync);
        CheckUpdateCommand = CreateCommand(CheckUpdateAsync);

        // 清理页与设置页共享同一份配置，任一处保存后都刷新本页文本，避免互相覆盖。
        Services.Settings.Changed += OnSettingsChanged;
    }

    public ICommand SaveCommand { get; }
    public ICommand BrowseExportCommand { get; }
    public ICommand BrowseBackupCommand { get; }
    public ICommand CheckUpdateCommand { get; }

    public int ThemeIndex { get => _themeIndex; set => Set(ref _themeIndex, Math.Clamp(value, 0, 2)); }
    public int LanguageIndex { get => _languageIndex; set => Set(ref _languageIndex, Math.Clamp(value, 0, 1)); }
    public int RefreshIntervalMs { get => _refreshIntervalMs; set => Set(ref _refreshIntervalMs, Math.Clamp(value, 250, 60_000)); }
    // Math.Clamp(NaN, ...) 返回 NaN，保存时 JsonSerializer 拒绝写 NaN 抛异常。
    // 非有限值（NaN / ±∞）一律回退默认值，保证配置恒可序列化。
    public double TemperatureThreshold
    {
        get => _temperatureThreshold;
        set => Set(ref _temperatureThreshold,
            double.IsFinite(value) ? Math.Clamp(value, 30, 120) : 85);
    }
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
        _lastUpdateInfo = info;
        UpdateMessage = BuildUpdateMessage(info);
        StatusMessage = UpdateMessage;
    }

    /// <summary>
    /// 从共享设置重新加载文本字段（主题 / 语言 / 间隔 / 阈值等数值项不重载，
    /// 以免覆盖用户尚未保存的编辑）；值相同则不更新，避免自己保存触发的回环。
    /// </summary>
    private bool ReloadTextFields()
    {
        var settings = Services.Settings.Current;
        var exportPath = settings.DefaultExportDirectory;
        var backupPath = settings.RegistryBackupDirectory;
        var updateUrl = settings.UpdateCheckUrl;
        var whitelist = string.Join(Environment.NewLine, settings.Whitelist.Select(r => r.Pattern));
        var customPaths = string.Join(Environment.NewLine, settings.CustomScanPaths);
        var changed = false;
        if (!string.Equals(_exportPath, exportPath, StringComparison.Ordinal)) { _exportPath = exportPath; changed = true; }
        if (!string.Equals(_backupPath, backupPath, StringComparison.Ordinal)) { _backupPath = backupPath; changed = true; }
        if (!string.Equals(_updateUrl, updateUrl, StringComparison.Ordinal)) { _updateUrl = updateUrl; changed = true; }
        if (!string.Equals(_whitelistText, whitelist, StringComparison.Ordinal)) { _whitelistText = whitelist; changed = true; }
        if (!string.Equals(_customPathsText, customPaths, StringComparison.Ordinal)) { _customPathsText = customPaths; changed = true; }
        return changed;
    }

    private void OnSettingsChanged()
    {
        var changed = ReloadTextFields();
        // 主题 / 语言是「即时生效」字段：顶栏切换会立即 Settings.Update 落盘，
        // 若设置页缓存的选中项不跟随，用户在设置页点保存时就会用旧值把顶栏的改动覆盖回去。
        // 刷新间隔 / 阈值是「保存才生效」，仍不在此重载，以免覆盖用户尚未保存的编辑。
        var settings = Services.Settings.Current;
        var theme = (int)settings.Theme;
        var language = (int)settings.Language;
        if (_themeIndex != theme) { _themeIndex = theme; changed = true; }
        if (_languageIndex != language) { _languageIndex = language; changed = true; }
        if (changed) RaiseAll();
    }

    private string BuildUpdateMessage(SystemAssistant.Models.Common.AppVersionInfo info) =>
        info.HasUpdate
            ? LF("Update_Available", info.LatestVersion, info.CurrentVersion) + "  " + info.ReleaseNotes
            : info.Message;

    protected override void OnLanguageChanged()
    {
        if (_lastUpdateInfo is not null) UpdateMessage = BuildUpdateMessage(_lastUpdateInfo);
    }

    public override void Dispose()
    {
        Services.Settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    private static IReadOnlyList<string> ParseLines(string text)
        => (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
