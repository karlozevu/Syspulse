using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;

namespace SystemAssistant.App.Views;

public static partial class PageViewLocator
{
    private static Control BuildOverviewPage(OverviewViewModel vm)
    {
        var toolbar = UiFactory.Toolbar(
            UiFactory.Button("Overview_Refresh", vm.RefreshCommand, accent: true),
            UiFactory.Button("Overview_ExportTxt", vm.ExportCommand, "txt"),
            UiFactory.Button("Overview_ExportMd", vm.ExportCommand, "md"),
            UiFactory.Button("Overview_ExportHtml", vm.ExportCommand, "html"));

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(toolbar);
        body.Children.Add(InfoCard("Overview_System",
            ("Overview_OsName", nameof(OverviewViewModel.OsName)),
            ("Overview_Version", nameof(OverviewViewModel.OsVersion)),
            ("Overview_Build", nameof(OverviewViewModel.Build)),
            ("Overview_Arch", nameof(OverviewViewModel.Architecture)),
            ("Overview_InstallDate", nameof(OverviewViewModel.InstallDate)),
            ("Overview_BootTime", nameof(OverviewViewModel.BootTime)),
            ("Overview_Computer", nameof(OverviewViewModel.ComputerName)),
            ("Overview_User", nameof(OverviewViewModel.UserName)),
            ("Overview_GeneratedAt", nameof(OverviewViewModel.GeneratedAt))));
        body.Children.Add(InfoCard("Overview_Mainboard",
            ("Overview_MainboardVendor", nameof(OverviewViewModel.MainboardVendor)),
            ("Overview_MainboardModel", nameof(OverviewViewModel.MainboardModel)),
            ("Overview_BiosVendor", nameof(OverviewViewModel.BiosVendor)),
            ("Overview_BiosVersion", nameof(OverviewViewModel.BiosVersion)),
            ("Overview_BiosDate", nameof(OverviewViewModel.BiosDate))));
        body.Children.Add(InfoCard("Overview_Cpu", ("Common_Value", nameof(OverviewViewModel.CpuSummary))));
        body.Children.Add(InfoCard("Overview_Memory", ("Common_Value", nameof(OverviewViewModel.MemorySummary))));
        body.Children.Add(InfoCard("Overview_Gpu", ("Common_Value", nameof(OverviewViewModel.GpuSummary))));
        body.Children.Add(InfoCard("Overview_Disks", ("Common_Value", nameof(OverviewViewModel.DiskSummary))));
        body.Children.Add(InfoCard("Overview_Network", ("Common_Value", nameof(OverviewViewModel.NetworkSummary))));
        body.Children.Add(InfoCard("Overview_Battery", ("Common_Value", nameof(OverviewViewModel.BatterySummary))));
        body.Children.Add(InfoCard("Overview_Display", ("Common_Value", nameof(OverviewViewModel.DisplaySummary))));

        return Shell(vm, "Overview_Title", "Overview_Subtitle", body);
    }

    private static Control BuildHardwarePage(HardwareViewModel vm)
    {
        var sensorButton = UiFactory.Button(vm.SensorButtonKey, vm.ToggleSensorCommand);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HardwareViewModel.SensorButtonKey))
                sensorButton.Content = LocalizationService.Instance.Get(vm.SensorButtonKey);
        };

        var toolbar = UiFactory.Toolbar(
            UiFactory.Button("Common_Refresh", vm.RefreshCommand, accent: true),
            sensorButton,
            UiFactory.Plain(""));

        var cpu = new StackPanel();
        cpu.Children.AddRange(new[]
        {
            UiFactory.InfoRow("Hardware_CpuName", nameof(HardwareViewModel.CpuName)),
            UiFactory.InfoRow("Hardware_CpuCores", nameof(HardwareViewModel.CpuCores)),
            UiFactory.InfoRow("Hardware_CpuBase", nameof(HardwareViewModel.CpuBase)),
            UiFactory.InfoRow("Hardware_CpuTurbo", nameof(HardwareViewModel.CpuTurbo)),
            UiFactory.InfoRow("Hardware_CpuCache", nameof(HardwareViewModel.CpuCache)),
            UiFactory.InfoRow("Hardware_CpuUsage", nameof(HardwareViewModel.CpuUsage)),
            UiFactory.InfoRow("Hardware_CpuClock", nameof(HardwareViewModel.CpuClock)),
            UiFactory.InfoRow("Hardware_CpuTemp", nameof(HardwareViewModel.CpuTemp)),
            UiFactory.InfoRow("Hardware_CpuPower", nameof(HardwareViewModel.CpuPower))
        });

        var memory = new StackPanel();
        memory.Children.AddRange(new[]
        {
            UiFactory.InfoRow("Hardware_MemTotal", nameof(HardwareViewModel.MemTotal)),
            UiFactory.InfoRow("Hardware_MemUsed", nameof(HardwareViewModel.MemUsed)),
            UiFactory.InfoRow("Hardware_MemFree", nameof(HardwareViewModel.MemFree)),
            UiFactory.InfoRow("Hardware_MemPage", nameof(HardwareViewModel.MemPage)),
            UiFactory.InfoRow("Hardware_MemChannel", nameof(HardwareViewModel.MemChannel)),
            UiFactory.InfoRow("Hardware_MemModules", nameof(HardwareViewModel.MemModules))
        });

        var gpu = UiFactory.BoundText(vm, nameof(HardwareViewModel.GpuList), mono: true);
        var disks = UiFactory.BoundText(vm, nameof(HardwareViewModel.DiskList), mono: true);

        var chartsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        var palette = new IBrush[]
        {
            Brushes.DodgerBlue, Brushes.OrangeRed, Brushes.MediumSeaGreen,
            Brushes.MediumPurple, Brushes.DarkOrange, Brushes.Teal
        };
        vm.ChartsUpdated += charts =>
        {
            chartsPanel.Children.Clear();
            foreach (var chart in charts)
            {
                var control = new LineChartControl
                {
                    Width = 320,
                    Margin = new Thickness(0, 0, 12, 12)
                };
                control.SetSeries(chart.Values, chart.Title, chart.Unit,
                    palette[chart.ColorIndex % palette.Length]);
                chartsPanel.Children.Add(control);
            }
        };
        var sensors = new StackPanel { Spacing = 8 };
        sensors.Children.Add(UiFactory.Muted("Hardware_SensorsHint"));
        sensors.Children.Add(UiFactory.Toolbar(
            UiFactory.Button("Common_Refresh", vm.RefreshCommand),
            UiFactory.Plain("")));
        sensors.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                UiFactory.Plain(LocalizationService.Instance.Get("Hardware_SensorsCount") + ": "),
                UiFactory.BoundText(vm, nameof(HardwareViewModel.SensorCount))
            }
        });
        sensors.Children.Add(chartsPanel);

        var battery = new StackPanel();
        battery.Children.AddRange(new[]
        {
            UiFactory.InfoRow("Hardware_BatteryDesign", nameof(HardwareViewModel.BatteryDesign)),
            UiFactory.InfoRow("Hardware_BatteryFull", nameof(HardwareViewModel.BatteryFull)),
            UiFactory.InfoRow("Hardware_BatteryWear", nameof(HardwareViewModel.BatteryWear)),
            UiFactory.InfoRow("Hardware_BatteryCycles", nameof(HardwareViewModel.BatteryCycles)),
            UiFactory.InfoRow("Hardware_BatteryCharge", nameof(HardwareViewModel.BatteryCharge))
        });

        var displays = UiFactory.BoundText(vm, nameof(HardwareViewModel.DisplayList), mono: true);

        var tabs = UiFactory.Tabs(
            ("Hardware_TabCpu", UiFactory.Card(cpu)),
            ("Hardware_TabMemory", UiFactory.Card(memory)),
            ("Hardware_TabGpu", UiFactory.Card(gpu)),
            ("Hardware_TabDisk", UiFactory.Card(disks)),
            ("Hardware_TabSensors", UiFactory.Card(sensors)),
            ("Hardware_TabBattery", UiFactory.Card(battery)),
            ("Hardware_TabDisplay", UiFactory.Card(displays)));

        var body = new StackPanel { Children = { toolbar, tabs } };
        return Shell(vm, "Hardware_Title", null, body);
    }
}
