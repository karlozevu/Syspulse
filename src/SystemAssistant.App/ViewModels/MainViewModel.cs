using System.Windows.Input;
using Avalonia.Threading;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.App.Views;
using SystemAssistant.App.Views.Dialogs;
using SystemAssistant.Core.Common;
using SystemAssistant.Models.Config;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.App.ViewModels;

/// <summary>页面标识。</summary>
public enum PageKind
{
    Overview, Hardware, Cleaner, Optimization, Processes, Toolbox, Settings, Logs
}

/// <summary>侧边栏导航项。</summary>
public sealed class NavigationItem
{
    public required PageKind Kind { get; init; }
    public required string Icon { get; init; }
    public required string LabelKey { get; init; }
    public string Group { get; init; } = string.Empty;
}

/// <summary>
/// 主窗口 ViewModel：负责导航、主题 / 语言切换、权限状态、更新检测与阈值告警。
/// 具体业务全部下沉到各页面 ViewModel 与 Core 服务。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private PageKind _selectedKind;
    private ViewModelBase? _currentPage;
    private string _globalStatus = string.Empty;
    private string _updateNote = string.Empty;
    private int _themeIndex;
    private int _languageIndex;
    private readonly Dictionary<PageKind, ViewModelBase> _pages = new();
    private readonly Action<ThemeMode> _themeHandler;
    private readonly EventHandler _languageHandler;
    private volatile bool _alertShowing;
    private DateTime _lastAlertUtc = DateTime.MinValue;

    public MainWindowViewModel()
    {
        var settings = Services.Settings.Current;
        _themeIndex = (int)settings.Theme;
        _languageIndex = (int)settings.Language;
        LocalizationService.Instance.SetLanguage(settings.Language);
        ThemeService.Apply(settings.Theme);

        // 具名委托，Dispose 时逐一解绑，避免静态事件把整个 ViewModel 钉在内存里。
        _themeHandler = mode => Set(ref _themeIndex, (int)mode, nameof(ThemeIndex));
        _languageHandler = (_, _) =>
        {
            Set(ref _languageIndex, (int)LocalizationService.Instance.Language, nameof(LanguageIndex));
            UpdateGlobalStatus();
        };
        ThemeService.ThemeChanged += _themeHandler;
        LocalizationService.Instance.LanguageChanged += _languageHandler;

        NavigationItems = new[]
        {
            new NavigationItem { Kind = PageKind.Overview, Icon = "▤", LabelKey = "Nav_Overview", Group = "Nav_Group_Main" },
            new NavigationItem { Kind = PageKind.Hardware, Icon = "◈", LabelKey = "Nav_Hardware", Group = "Nav_Group_Main" },
            new NavigationItem { Kind = PageKind.Processes, Icon = "◫", LabelKey = "Nav_Process", Group = "Nav_Group_Main" },
            new NavigationItem { Kind = PageKind.Cleaner, Icon = "⌫", LabelKey = "Nav_Cleaner", Group = "Nav_Group_Tools" },
            new NavigationItem { Kind = PageKind.Optimization, Icon = "⚙", LabelKey = "Nav_Optimize", Group = "Nav_Group_Tools" },
            new NavigationItem { Kind = PageKind.Toolbox, Icon = "⊞", LabelKey = "Nav_Toolbox", Group = "Nav_Group_Tools" },
            new NavigationItem { Kind = PageKind.Settings, Icon = "☰", LabelKey = "Nav_Settings", Group = "Nav_Group_System" },
            new NavigationItem { Kind = PageKind.Logs, Icon = "▩", LabelKey = "Nav_Logs", Group = "Nav_Group_System" }
        };

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is NavigationItem item) Navigate(item.Kind);
        });
        ElevateCommand = new RelayCommand(() =>
        {
            if (PrivilegeHelper.IsAdministrator) return;
            // 状态栏绑定的是 GlobalStatus，写 StatusMessage 用户永远看不到。
            if (!DialogService.Elevate()) GlobalStatus = L("Status_UacCancelled");
        });
        OpenDashboardCommand = new RelayCommand(() =>
        {
            var dashboard = FloatingDashboardWindow.Current;
            if (dashboard is null) new FloatingDashboardWindow().Show();
            else dashboard.Activate();
        });

        NavigateTo(PageKind.Overview);
        Services.Sensors.ThresholdExceeded += OnThresholdExceeded;
        Services.Sensors.Start();
        Services.Processes.Start();
        UpdateGlobalStatus();
        _ = CheckUpdateOnStartupAsync(LifetimeToken);
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }
    public ICommand NavigateCommand { get; }
    public ICommand ElevateCommand { get; }
    public ICommand OpenDashboardCommand { get; }

    public ViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public PageKind SelectedKind
    {
        get => _selectedKind;
        private set => Set(ref _selectedKind, value);
    }

    public string GlobalStatus
    {
        get => _globalStatus;
        private set => Set(ref _globalStatus, value);
    }

    public bool IsAdministrator => PrivilegeHelper.IsAdministrator;

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!Set(ref _themeIndex, value)) return;
            var theme = (ThemeMode)Math.Clamp(value, 0, 2);
            ThemeService.Apply(theme);
            Services.Settings.Update(s => s.Theme = theme);
        }
    }

    public int LanguageIndex
    {
        get => _languageIndex;
        set
        {
            if (!Set(ref _languageIndex, value)) return;
            var language = (LanguageMode)Math.Clamp(value, 0, 1);
            LocalizationService.Instance.SetLanguage(language);
            Services.Settings.Update(s => s.Language = language);
            UpdateGlobalStatus();
        }
    }

    private void Navigate(PageKind kind) => NavigateTo(kind);

    private void NavigateTo(PageKind kind)
    {
        SelectedKind = kind;
        if (!_pages.TryGetValue(kind, out var vm))
        {
            vm = CreatePage(kind);
            _pages[kind] = vm;
        }
        CurrentPage = vm;
    }

    private static ViewModelBase CreatePage(PageKind kind) => kind switch
    {
        PageKind.Overview => new OverviewViewModel(),
        PageKind.Hardware => new HardwareViewModel(),
        PageKind.Cleaner => new CleanerViewModel(),
        PageKind.Optimization => new OptimizationViewModel(),
        PageKind.Processes => new ProcessViewModel(),
        PageKind.Toolbox => new ToolboxViewModel(),
        PageKind.Settings => new SettingsViewModel(),
        PageKind.Logs => new LogsViewModel(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>阈值告警：同类告警 60 秒内只弹一次，弹窗期间不叠加。</summary>
    private void OnThresholdExceeded(ThresholdAlert alert)
    {
        if (_alertShowing || (DateTime.UtcNow - _lastAlertUtc).TotalSeconds < 60) return;
        _alertShowing = true;
        _lastAlertUtc = DateTime.UtcNow;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var message = LF("Sensors_AlertBody", alert.Reading.DisplayName, alert.Reading.Value,
                    alert.Reading.Unit, alert.Threshold);
                await DialogService.MessageAsync(L("Sensors_AlertTitle"), message);
            }
            catch (Exception ex)
            {
                GlobalStatus = L("Message_ErrorPrefix") + ex.Message;
            }
            finally { _alertShowing = false; }
        });
    }

    private async Task CheckUpdateOnStartupAsync(CancellationToken token)
    {
        try
        {
            // 仅检测；未配置 URL 时 UpdateCheckService 直接返回本地结果，不发出任何网络请求。
            var info = await Services.Update.CheckAsync(token);
            if (token.IsCancellationRequested) return;
            _updateNote = info.HasUpdate
                ? LF("Update_Available", info.LatestVersion, info.CurrentVersion)
                : info.Message;
            UpdateGlobalStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _updateNote = ex.Message;
            UpdateGlobalStatus();
        }
    }

    protected override void OnLanguageChanged() => UpdateGlobalStatus();

    private void UpdateGlobalStatus()
    {
        var admin = PrivilegeHelper.IsAdministrator ? L("Common_AdminOk") : L("Common_AdminRequired");
        // 版本 + 权限始终可见，更新检测结果作为附加信息，不覆盖权限状态。
        GlobalStatus = string.IsNullOrWhiteSpace(_updateNote)
            ? $"v{Services.Update.CurrentVersion} · {admin}"
            : $"v{Services.Update.CurrentVersion} · {admin} · {_updateNote}";
    }

    public override void Dispose()
    {
        ThemeService.ThemeChanged -= _themeHandler;
        LocalizationService.Instance.LanguageChanged -= _languageHandler;
        Services.Sensors.ThresholdExceeded -= OnThresholdExceeded;
        Services.Sensors.Stop();
        Services.Processes.Stop();
        foreach (var page in _pages.Values) page.Dispose();
        base.Dispose();
    }
}

/// <summary>悬浮仪表盘 ViewModel：只展示全局 CPU / 内存占用。</summary>
public sealed class FloatingDashboardViewModel : ObservableObject
{
    private double _cpuUsage;
    private double _memoryUsage;
    private string _cpuText = "0.0 %";
    private string _memoryText = "0.0 %";

    public FloatingDashboardViewModel()
    {
        ServiceHub.Instance.Sensors.SnapshotUpdated += OnSnapshot;
    }

    public double CpuUsage
    {
        get => _cpuUsage;
        set => Set(ref _cpuUsage, value);
    }

    public double MemoryUsage
    {
        get => _memoryUsage;
        set => Set(ref _memoryUsage, value);
    }

    public string CpuText
    {
        get => _cpuText;
        set => Set(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        set => Set(ref _memoryText, value);
    }

    private void OnSnapshot(SensorSnapshot snapshot)
    {
        // CPU 占用优先用 GetSystemTimes，内存用已用物理内存比例；两者都在后台线程计算好后推到 UI
        var cpu = snapshot.CpuUsagePercent ?? WindowsNativeInfo.GetCpuUsagePercent() ?? CpuUsage;
        double memory = snapshot.MemoryUsagePercent ?? 0;
        if (snapshot.MemoryUsagePercent is null)
        {
            var (total, available, _, _) = WindowsNativeInfo.GetMemoryStatus();
            if (total > 0) memory = (total - available) * 100.0 / total;
        }
        Dispatcher.UIThread.Post(() =>
        {
            CpuUsage = cpu;
            MemoryUsage = memory;
            CpuText = $"{CpuUsage:F1} %";
            MemoryText = $"{MemoryUsage:F1} %";
        });
    }

    public void Detach() => ServiceHub.Instance.Sensors.SnapshotUpdated -= OnSnapshot;
}
