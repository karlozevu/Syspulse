using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SystemAssistant.App.Controls;
using SystemAssistant.App.Services;
using SystemAssistant.App.ViewModels;
using SystemAssistant.Models.Common;
using SystemAssistant.Models.Toolbox;
using SystemAssistant.Utils.Common;

namespace SystemAssistant.App.Views;

public static partial class PageViewLocator
{
    // -------------------------------------------------------------- 工具箱页
    private static Control BuildToolboxPage(ToolboxViewModel vm)
    {
        var progress = ProgressBlock(vm, nameof(ToolboxViewModel.ProgressPercent), nameof(ToolboxViewModel.ProgressText));

        // 文件粉碎
        var passes = UiFactory.BoundInput(vm, nameof(ToolboxViewModel.Passes));
        passes.Width = 80;
        var shredFiles = UiFactory.BoundText(vm, nameof(ToolboxViewModel.ShredFileList));
        shredFiles.FontFamily = new Avalonia.Media.FontFamily("Consolas, Courier New, monospace");
        var shred = new StackPanel
        {
            Children =
            {
                UiFactory.Muted("Toolbox_ShredHint"),
                UiFactory.Toolbar(
                    UiFactory.Button("Toolbox_SelectFiles", vm.PickFilesCommand),
                    UiFactory.Plain(LocalizationService.Instance.Get("Toolbox_Passes") + ":"),
                    passes,
                    UiFactory.Button("Toolbox_Shred", vm.ShredCommand, accent: true)),
                UiFactory.Card(shredFiles)
            }
        };

        // 大文件查找
        var rootLarge = UiFactory.BoundInput(vm, nameof(ToolboxViewModel.RootPath));
        rootLarge.Width = 320;
        var minLarge = UiFactory.BoundInput(vm, nameof(ToolboxViewModel.MinSizeMb));
        minLarge.Width = 80;
        var largeTable = new TableView { Height = 400 };
        largeTable.Configure(new[]
        {
            Col("Common_Name", 220, o => ((FileEntry)o).FileName),
            Col("Common_Size", 110, o => FormatHelper.FormatBytes(((FileEntry)o).SizeBytes)),
            Col("Common_Path", 480, o => ((FileEntry)o).DirectoryName, stretch: true)
        }, withCheckBox: true,
            getChecked: o => ((FileEntry)o).IsChecked,
            setChecked: (o, value) => ((FileEntry)o).IsChecked = value);
        largeTable.RowDoubleTapped += item =>
        {
            if (item is FileEntry entry) SystemAssistant.Utils.IO.FileSystemHelper.OpenInExplorer(entry.FullPath);
        };
        var large = new StackPanel
        {
            Children =
            {
                UiFactory.Toolbar(
                    UiFactory.Button("Common_Browse", vm.PickRootCommand),
                    rootLarge,
                    UiFactory.Plain(LocalizationService.Instance.Get("Toolbox_MinSize") + ":"),
                    minLarge,
                    UiFactory.Button("Toolbox_FindLarge", vm.FindLargeCommand, accent: true),
                    UiFactory.Button("Toolbox_DeleteSelected", vm.DeleteSelectedCommand, "large")),
                largeTable
            }
        };

        // 重复文件查找
        var rootDuplicate = UiFactory.BoundInput(vm, nameof(ToolboxViewModel.RootPath));
        rootDuplicate.Width = 320;
        var duplicateTable = new TableView { Height = 400 };
        duplicateTable.Configure(new[]
        {
            Col("Common_Name", 200, o => ((DuplicateRow)o).File.FileName),
            Col("Common_Size", 100, o => FormatHelper.FormatBytes(((DuplicateRow)o).File.SizeBytes)),
            Col("Common_Value", 130, o => ((DuplicateRow)o).Hash[..Math.Min(12, ((DuplicateRow)o).Hash.Length)]),
            Col("Common_Path", 460, o => ((DuplicateRow)o).File.DirectoryName, stretch: true)
        }, withCheckBox: true,
            getChecked: o => ((DuplicateRow)o).IsChecked,
            setChecked: (o, value) => ((DuplicateRow)o).IsChecked = value);
        duplicateTable.RowDoubleTapped += item =>
        {
            if (item is DuplicateRow row) SystemAssistant.Utils.IO.FileSystemHelper.OpenInExplorer(row.File.FullPath);
        };
        var duplicate = new StackPanel
        {
            Children =
            {
                UiFactory.Toolbar(
                    UiFactory.Button("Common_Browse", vm.PickRootCommand),
                    rootDuplicate,
                    UiFactory.Button("Toolbox_FindDuplicates", vm.FindDuplicatesCommand, accent: true),
                    UiFactory.Button("Toolbox_DeleteSelected", vm.DeleteSelectedCommand, "duplicate")),
                UiFactory.BoundText(vm, nameof(ToolboxViewModel.WastedText)),
                duplicateTable
            }
        };

        // 系统快捷入口
        var shortcutKeys = new (string Key, string Tool)[]
        {
            ("Toolbox_Taskmgr", "taskmgr"), ("Toolbox_Regedit", "regedit"),
            ("Toolbox_Cmd", "cmd"), ("Toolbox_Powershell", "powershell"),
            ("Toolbox_Services", "services")
        };
        var shortcutPanel = new WrapPanel();
        foreach (var (key, tool) in shortcutKeys)
        {
            var button = UiFactory.Button(key, vm.ShortcutCommand, tool);
            button.Margin = new Thickness(0, 0, 10, 10);
            shortcutPanel.Children.Add(button);
        }
        var shortcuts = UiFactory.Section("Toolbox_TabShortcuts", shortcutPanel);

        vm.ResultsChanged += () =>
        {
            largeTable.SetItems(vm.LargeFiles);
            duplicateTable.SetItems(vm.DuplicateRows);
        };

        var tabs = UiFactory.Tabs(
            ("Toolbox_TabShred", shred),
            ("Toolbox_TabLarge", large),
            ("Toolbox_TabDuplicate", duplicate),
            ("Toolbox_TabShortcuts", shortcuts));
        var body = new StackPanel { Children = { progress, tabs } };
        return Shell(vm, "Toolbox_Title", null, body);
    }

    // -------------------------------------------------------------- 设置页
    private static Control BuildSettingsPage(SettingsViewModel vm)
    {
        var themeCombo = UiFactory.LocalizedCombo(
            new[] { "Theme_System", "Theme_Light", "Theme_Dark" },
            vm.ThemeIndex, index => vm.ThemeIndex = index);
        var languageCombo = UiFactory.LocalizedCombo(
            new[] { "Lang_ZhCn", "Lang_EnUs" },
            vm.LanguageIndex, index => vm.LanguageIndex = index);

        var appearance = UiFactory.Section("Settings_Appearance",
            LabeledRow("Settings_Theme", themeCombo),
            LabeledRow("Settings_Language", languageCombo));

        var interval = UiFactory.BoundInput(vm, nameof(SettingsViewModel.RefreshIntervalMs));
        interval.Width = 160;
        var threshold = UiFactory.BoundInput(vm, nameof(SettingsViewModel.TemperatureThreshold));
        threshold.Width = 160;
        var monitoring = UiFactory.Section("Settings_Monitor",
            LabeledRow("Settings_Interval", interval),
            LabeledRow("Settings_Thresholds", threshold));

        var exportPath = UiFactory.BoundInput(vm, nameof(SettingsViewModel.ExportPath));
        exportPath.Width = 420;
        var backupPath = UiFactory.BoundInput(vm, nameof(SettingsViewModel.BackupPath));
        backupPath.Width = 420;
        var updateUrl = UiFactory.BoundInput(vm, nameof(SettingsViewModel.UpdateUrl));
        updateUrl.Width = 420;

        var whitelist = UiFactory.BoundInput(vm, nameof(SettingsViewModel.WhitelistText), multiline: true, height: 110);
        var customPaths = UiFactory.BoundInput(vm, nameof(SettingsViewModel.CustomPathsText), multiline: true, height: 90);

        var paths = UiFactory.Section("Settings_Paths",
            LabeledRow("Settings_ExportPath", UiFactory.Toolbar(exportPath,
                UiFactory.Button("Common_Browse", vm.BrowseExportCommand))),
            LabeledRow("Settings_BackupPath", UiFactory.Toolbar(backupPath,
                UiFactory.Button("Common_Browse", vm.BrowseBackupCommand))),
            UiFactory.Plain(LocalizationService.Instance.Get("Settings_Whitelist"), muted: true),
            whitelist,
            UiFactory.Plain(LocalizationService.Instance.Get("Settings_CustomPaths"), muted: true),
            customPaths);

        var update = UiFactory.Section("Settings_Update",
            LabeledRow("Settings_UpdateUrl", UiFactory.Toolbar(updateUrl,
                UiFactory.Button("Settings_CheckUpdate", vm.CheckUpdateCommand))),
            UiFactory.BoundText(vm, nameof(SettingsViewModel.UpdateMessage), muted: true));

        var body = new StackPanel
        {
            Children =
            {
                UiFactory.Toolbar(UiFactory.Button("Common_Save", vm.SaveCommand, accent: true)),
                appearance, monitoring, paths, update
            }
        };
        return Shell(vm, "Settings_Title", null, body);
    }

    /// <summary>左标签 + 右控件的表单行。</summary>
    private static Control LabeledRow(string labelKey, Control value)
    {
        var label = UiFactory.Label(labelKey);
        label.Width = 240;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 5) };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    // -------------------------------------------------------------- 日志页
    private static Control BuildLogsPage(LogsViewModel vm)
    {
        var table = new TableView { Height = 500 };
        table.Configure(new[]
        {
            Col("Logs_ColTime", 150, o => ((OperationLogEntry)o).Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
            Col("Logs_ColType", 110, o => ((OperationLogEntry)o).Type.ToString()),
            Col("Logs_ColAction", 150, o => ((OperationLogEntry)o).Action),
            Col("Logs_ColDetail", 520, o => ((OperationLogEntry)o).Detail, stretch: true),
            Col("Logs_ColSuccess", 80, o => ((OperationLogEntry)o).Success
                ? LocalizationService.Instance.Get("Common_Yes")
                : LocalizationService.Instance.Get("Common_No"))
        });
        table.RowDoubleTapped += item =>
        {
            if (item is OperationLogEntry entry)
                SystemAssistant.App.Services.DialogService.MessageAsync(LocalizationService.Instance.Get("Logs_ColDetail"),
                    entry.Detail + Environment.NewLine + entry.ErrorMessage);
        };
        vm.EntriesChanged += () => table.SetItems(vm.Entries);

        var toolbar = UiFactory.Toolbar(
            UiFactory.Button("Common_Refresh", vm.RefreshCommand),
            UiFactory.Button("Logs_Export", vm.ExportCommand),
            UiFactory.Button("Logs_Clear", vm.ClearCommand));
        var body = new StackPanel { Children = { toolbar, table } };
        return Shell(vm, "Logs_Title", "Logs_Subtitle", body);
    }
}
