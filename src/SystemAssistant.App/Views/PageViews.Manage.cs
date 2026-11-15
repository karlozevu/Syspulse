using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;
using SystemAssistant.Models.Cleaning;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Models.Processes;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.Views;

public static partial class PageViewLocator
{
    // ---------------------------------------------------------------- 清理页
    private static Control BuildCleanerPage(CleanerViewModel vm)
    {
        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 0, 0, 8) };
        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(UiFactory.BoundCheck("Cleaner_SystemJunk", vm, nameof(CleanerViewModel.ScanSystemJunk)));
        left.Children.Add(UiFactory.BoundCheck("Cleaner_Browser", vm, nameof(CleanerViewModel.ScanBrowserCache)));
        left.Children.Add(UiFactory.BoundCheck("Cleaner_AppCache", vm, nameof(CleanerViewModel.ScanApplicationCache)));
        left.Children.Add(UiFactory.BoundCheck("Cleaner_Registry", vm, nameof(CleanerViewModel.ScanRegistryResidue)));
        var right = new StackPanel { Spacing = 6 };
        right.Children.Add(UiFactory.BoundCheck("Cleaner_History", vm, nameof(CleanerViewModel.ScanBrowserHistory)));
        right.Children.Add(UiFactory.BoundCheck("Cleaner_Cookies", vm, nameof(CleanerViewModel.ScanBrowserCookies)));
        right.Children.Add(UiFactory.BoundCheck("Cleaner_Downloads", vm, nameof(CleanerViewModel.ScanBrowserDownloads)));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        options.Children.Add(left);
        options.Children.Add(right);

        var toolbar = UiFactory.Toolbar(
            UiFactory.Button("Cleaner_Scan", vm.ScanCommand, accent: true),
            UiFactory.Button("Cleaner_Execute", vm.CleanCommand, variant: ButtonVariant.Danger),
            UiFactory.Button("Cleaner_Backup", vm.BackupRegistryCommand),
            UiFactory.Button("Common_UnselectAll", vm.SelectNoneCommand));

        var groupsPanel = new StackPanel { Spacing = 8 };
        vm.GroupsChanged += () =>
        {
            groupsPanel.Children.Clear();
            foreach (var group in vm.Groups)
            {
                var table = new TableView { Height = 230 };
                table.Configure(new[]
                {
                    Col("Common_Name", 200, o => ((ScanItem)o).DisplayName),
                    Col("Common_Size", 100, o => FormatHelper.FormatBytes(((ScanItem)o).SizeBytes)),
                    Col("Common_Risk", 70, o => ((ScanItem)o).Risk switch
                    {
                        RiskLevel.High => LocalizationService.Instance.Get("Risk_High"),
                        RiskLevel.Medium => LocalizationService.Instance.Get("Risk_Medium"),
                        _ => LocalizationService.Instance.Get("Risk_Low")
                    }),
                    Col("Common_Path", 500, o => ((ScanItem)o).Path, stretch: true)
                }, withCheckBox: true,
                    getChecked: o => ((ScanItem)o).IsChecked,
                    setChecked: (o, value) => ((ScanItem)o).IsChecked = value);
                table.SetItems(group.Items);

                var expander = new Expander
                {
                    Header = $"{group.Title}   ({group.Items.Count} {LocalizationService.Instance.Get("Common_ItemsUnit")} / {FormatHelper.FormatBytes(group.TotalSizeBytes)})",
                    Content = table,
                    IsExpanded = group.Category is ScanCategory.SystemTemp or ScanCategory.BrowserCache,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                groupsPanel.Children.Add(expander);
            }
        };

        var summary = UiFactory.BoundText(vm, nameof(CleanerViewModel.Summary));
        var warnings = UiFactory.BoundText(vm, nameof(CleanerViewModel.WarningText), muted: true);
        var progress = ProgressBlock(vm, nameof(CleanerViewModel.ProgressPercent), nameof(CleanerViewModel.ProgressText));

        var customPaths = UiFactory.BoundInput(vm, nameof(CleanerViewModel.CustomPathsText), multiline: true, height: 70);
        var whitelist = UiFactory.BoundInput(vm, nameof(CleanerViewModel.WhitelistText), multiline: true, height: 70);

        var body = new StackPanel();
        body.Children.Add(UiFactory.Section("Cleaner_Groups", options, toolbar, summary, progress, warnings, groupsPanel));
        body.Children.Add(UiFactory.Section("Cleaner_CustomPaths", customPaths,
            UiFactory.Toolbar(UiFactory.Button("Common_Add", vm.AddCustomPathCommand))));
        body.Children.Add(UiFactory.Section("Cleaner_Whitelist", whitelist,
            UiFactory.Toolbar(UiFactory.Button("Common_Save", vm.SaveWhitelistCommand))));
        return Shell(vm, "Cleaner_Title", "Cleaner_Subtitle", body);
    }

    // ------------------------------------------------------------ 系统优化页
    private static Control BuildOptimizationPage(OptimizationViewModel vm)
    {
        // 每个页签都必须用各自实例（Avalonia 控件不能同时挂在多个父节点下）
        TextBlock AdminHint()
        {
            var hint = new TextBlock { Classes = { "warn" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            hint.Bind(TextBlock.TextProperty, new Binding(nameof(OptimizationViewModel.AdminHint)) { Source = vm });
            return hint;
        }

        // 启动项
        var startupTable = new TableView { Height = 380 };
        startupTable.Configure(new[]
        {
            Col("Common_Name", 180, o => ((StartupItem)o).Name),
            Col("Common_Value", 130, o => ((StartupItem)o).Publisher),
            Col("Common_Status", 90, o => ((StartupItem)o).IsEnabled
                ? LocalizationService.Instance.Get("Common_Enable") : LocalizationService.Instance.Get("Common_Disable")),
            Col("Common_Path", 420, o => ((StartupItem)o).Command, stretch: true)
        });
        startupTable.SelectionChanged += item => vm.SelectedStartup = item as StartupItem;
        startupTable.RowDoubleTapped += item =>
        {
            if (item is StartupItem startup) SystemAssistant.Utils.IO.FileSystemHelper.OpenInExplorer(startup.Command);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizationViewModel.StartupItems))
                startupTable.SetItems(vm.StartupItems);
        };
        var startup = new StackPanel
        {
            Children =
            {
                UiFactory.Toolbar(
                    UiFactory.Button("Common_Refresh", vm.RefreshStartupCommand),
                    UiFactory.Button("Common_Enable", vm.EnableStartupCommand),
                    UiFactory.Button("Common_Disable", vm.DisableStartupCommand),
                    UiFactory.Button("Common_Delete", vm.DeleteStartupCommand, variant: ButtonVariant.Danger)),
                startupTable
            }
        };

        // 服务
        var serviceTable = new TableView { Height = 330 };
        serviceTable.Configure(new[]
        {
            Col("Common_Name", 210, o => ((WindowsServiceInfo)o).DisplayName),
            Col("Common_Value", 170, o => ((WindowsServiceInfo)o).ServiceName),
            Col("Common_Status", 100, o => ((WindowsServiceInfo)o).State.ToString()),
            Col("Optimize_StartMode", 150, o => ((WindowsServiceInfo)o).StartMode.ToString()),
            Col("Common_Risk", 280, o => ((WindowsServiceInfo)o).IsHighRisk ? LocalizationService.Instance.Get("Optimize_HighRiskService") : string.Empty, stretch: true)
        });
        serviceTable.SelectionChanged += item => vm.SelectedService = item as WindowsServiceInfo;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizationViewModel.ServicesList))
                serviceTable.SetItems(vm.ServicesList);
        };
        var startModeCombo = new ComboBox { Width = 190 };
        foreach (var mode in vm.StartModes) startModeCombo.Items.Add(mode);
        startModeCombo.SelectedIndex = 2;
        var setModeButton = new Button
        {
            Command = vm.SetStartModeCommand,
            CommandParameter = "Manual"
        };
        SystemAssistant.App.Services.Localize.SetKey(setModeButton, "Optimize_StartMode");
        startModeCombo.SelectionChanged += (_, _) =>
            setModeButton.CommandParameter = startModeCombo.SelectedItem as string ?? "Manual";

        var riskHint = UiFactory.BoundText(vm, nameof(OptimizationViewModel.ServiceRiskHint));
        riskHint.Classes.Add("danger");
        var startServiceButton = UiFactory.Button("Optimize_Start", vm.StartServiceCommand);
        var stopServiceButton = UiFactory.Button("Optimize_Stop", vm.StopServiceCommand);
        startServiceButton.IsEnabled = vm.IsAdministrator;
        stopServiceButton.IsEnabled = vm.IsAdministrator;
        setModeButton.IsEnabled = vm.IsAdministrator;
        var services = new StackPanel
        {
            Children =
            {
                AdminHint(),
                UiFactory.Toolbar(
                    UiFactory.Button("Common_Refresh", vm.RefreshServicesCommand),
                    startServiceButton, stopServiceButton, startModeCombo, setModeButton),
                riskHint,
                serviceTable
            }
        };

        // 系统修复
        var consoleText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 280,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new Avalonia.Media.FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11
        };
        consoleText.Bind(TextBox.TextProperty, new Binding(nameof(OptimizationViewModel.ConsoleText)) { Source = vm });
        var repairButtons = UiFactory.Toolbar(
            UiFactory.Button("Optimize_Sfc", vm.SfcCommand, accent: true),
            UiFactory.Button("Optimize_Dism", vm.DismCommand),
            UiFactory.Button("Optimize_Console", new RelayCommand(() =>
            {
                var window = new SystemAssistant.App.Views.Dialogs.ConsoleWindow("Optimize_Console");
                void Handler(string line, bool isError) => window.Append(line, isError);
                vm.OutputLine += Handler;
                window.Closed += (_, _) => vm.OutputLine -= Handler;
                window.Show(ThemeService.GetMainWindow()!);
            })));
        foreach (var button in repairButtons.Children.OfType<Button>().Take(2)) button.IsEnabled = vm.IsAdministrator;
        var repair = new StackPanel { Children = { AdminHint(), repairButtons, consoleText } };

        // 磁盘维护
        var diskCombo = new ComboBox { Width = 320 };
        foreach (var item in vm.Disks)
            diskCombo.Items.Add($"Disk {item.DiskIndex} · {item.Model} [{item.MediaTypeText}]");
        diskCombo.SelectedIndex = vm.Disks.Count > 0 ? 0 : -1;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OptimizationViewModel.Disks)) return;
            diskCombo.Items.Clear();
            foreach (var item in vm.Disks)
                diskCombo.Items.Add($"Disk {item.DiskIndex} · {item.Model} [{item.MediaTypeText}]");
            if (vm.Disks.Count > 0) diskCombo.SelectedIndex = 0;
        };
        diskCombo.SelectionChanged += (_, _) =>
        {
            if (diskCombo.SelectedIndex >= 0 && diskCombo.SelectedIndex < vm.Disks.Count)
                vm.SelectedDisk = vm.Disks[diskCombo.SelectedIndex];
        };
        var diskButtons = UiFactory.Toolbar(
            UiFactory.Button("Optimize_Trim", vm.DiskMaintenanceCommand, "trim", accent: true),
            UiFactory.Button("Optimize_Defrag", vm.DiskMaintenanceCommand, "defrag"),
            UiFactory.Button("Optimize_Analyze", vm.DiskMaintenanceCommand, "analyze"));
        foreach (var button in diskButtons.Children.OfType<Button>()) button.IsEnabled = vm.IsAdministrator;
        var diskTab = new StackPanel { Children = { AdminHint(), UiFactory.Toolbar(diskCombo), diskButtons } };

        // 网络工具
        var pingHost = UiFactory.BoundInput(vm, nameof(OptimizationViewModel.PingHost));
        pingHost.Width = 220;
        var pingResult = UiFactory.BoundText(vm, nameof(OptimizationViewModel.PingResult));
        var networkButtons = UiFactory.Toolbar(
            UiFactory.Button("Optimize_FlushDns", vm.FlushDnsCommand),
            UiFactory.Button("Optimize_ResetWinsock", vm.ResetWinsockCommand),
            UiFactory.Button("Optimize_ResetTcpIp", vm.ResetTcpIpCommand));
        // 与服务 / 修复 / 磁盘维护页保持一致：非管理员禁用需要提权的网络重置命令。
        foreach (var button in networkButtons.Children.OfType<Button>()) button.IsEnabled = vm.IsAdministrator;
        var network = new StackPanel
        {
            Children =
            {
                AdminHint(),
                networkButtons,
                UiFactory.Toolbar(pingHost, UiFactory.Button("Optimize_Ping", vm.PingCommand)),
                pingResult
            }
        };

        // 简易调优
        var tuningPanel = new StackPanel { Spacing = 8 };
        void RebuildTuning()
        {
            tuningPanel.Children.Clear();
            foreach (var item in vm.TuningItems)
            {
                var card = UiFactory.Card(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = item.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        UiFactory.Plain(item.Description, muted: true),
                        new TextBlock { Text = item.RiskText, Classes = { item.Risk == TuningRisk.Dangerous ? "danger" : "warn" },
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        UiFactory.Toolbar(UiFactory.Button(
                            item.CurrentValue ? "Common_Disable" : "Common_Enable",
                            vm.ToggleTuningCommand, item))
                    },
                    Spacing = 4
                });
                tuningPanel.Children.Add(card);
            }
        }
        RebuildTuning();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizationViewModel.TuningItems)) RebuildTuning();
        };
        var tuning = new StackPanel
        {
            Children =
            {
                UiFactory.Muted("Optimize_TuningHint"),
                UiFactory.Section("Optimize_VirtualMemory",
                    UiFactory.BoundText(vm, nameof(OptimizationViewModel.VirtualMemory)),
                    UiFactory.Toolbar(UiFactory.Button("Optimize_OpenVirtualMemory", vm.OpenVirtualMemoryCommand))),
                tuningPanel
            }
        };

        var tabs = UiFactory.Tabs(
            ("Optimize_TabStartup", startup),
            ("Optimize_TabServices", services),
            ("Optimize_TabRepair", repair),
            ("Optimize_TabDisk", diskTab),
            ("Optimize_TabNetwork", network),
            ("Optimize_TabTuning", tuning));
        return Shell(vm, "Optimize_Title", "Optimize_Subtitle", tabs);
    }

    // -------------------------------------------------------------- 进程页
    private static Control BuildProcessPage(ProcessViewModel vm)
    {
        var table = new TableView { Height = 520 };
        table.Configure(new[]
        {
            Col("Process_ColPid", 70, o => ((ProcessSnapshot)o).ProcessId.ToString(), "ProcessId"),
            Col("Process_ColName", 180, o => ((ProcessSnapshot)o).Name, "Name"),
            Col("Process_ColCpu", 90, o => $"{((ProcessSnapshot)o).CpuPercent:F1}", "CpuPercent"),
            Col("Process_ColMemory", 110, o => FormatHelper.FormatBytes(((ProcessSnapshot)o).WorkingSetBytes), "WorkingSetBytes"),
            Col("Process_ColDiskRead", 110, o => FormatHelper.FormatSpeed(((ProcessSnapshot)o).DiskReadBytesPerSecond), "DiskReadBytesPerSecond"),
            Col("Process_ColDiskWrite", 110, o => FormatHelper.FormatSpeed(((ProcessSnapshot)o).DiskWriteBytesPerSecond), "DiskWriteBytesPerSecond"),
            Col("Process_ColUser", 160, o => ((ProcessSnapshot)o).UserName, "UserName", stretch: true)
        });
        table.SortRequested += vm.Sort;
        table.SelectionChanged += item => vm.Selected = item as ProcessSnapshot;
        table.RowDoubleTapped += _ => vm.DetailsCommand.Execute(vm.Selected);
        vm.ProcessesUpdated += () => table.SetItems(vm.Processes);

        var toolbar = UiFactory.Toolbar(
            UiFactory.Button("Process_Kill", vm.KillCommand, variant: ButtonVariant.Danger),
            UiFactory.Button("Process_KillTree", vm.KillTreeCommand, variant: ButtonVariant.Danger),
            UiFactory.Button("Process_Details", vm.DetailsCommand),
            UiFactory.Button("Process_Dashboard", vm.DashboardCommand));
        var body = new StackPanel { Children = { toolbar, table } };
        return Shell(vm, "Process_Title", null, body);
    }
}
