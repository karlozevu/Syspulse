using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Models.Toolbox;
using SystemAssistant.Utils.Common;
using SystemAssistant.Utils.IO;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.App.ViewModels;

/// <summary>重复文件表格行（把分组信息摊平，便于勾选）。</summary>
public sealed class DuplicateRow
{
    public required string Hash { get; init; }
    public required FileEntry File { get; init; }
    public bool IsChecked { get; set; }
}

/// <summary>独立工具箱 ViewModel：文件粉碎、大文件、重复文件、系统快捷入口。</summary>
public sealed class ToolboxViewModel : ViewModelBase
{
    private IReadOnlyList<string> _shredFiles = Array.Empty<string>();
    private int _passes = 3;
    private string _rootPath = "C:\\";
    private int _minSizeMb = 100;
    private IReadOnlyList<FileEntry> _largeFiles = Array.Empty<FileEntry>();
    private IReadOnlyList<DuplicateGroup> _duplicateGroups = Array.Empty<DuplicateGroup>();
    private IReadOnlyList<DuplicateRow> _duplicateRows = Array.Empty<DuplicateRow>();
    private string _shredFileList = string.Empty;
    private string _wastedText = string.Empty;
    private double _progressPercent;
    private string _progressText = string.Empty;

    public ToolboxViewModel()
    {
        PickFilesCommand = CreateCommand(PickFilesAsync);
        ShredCommand = CreateCommand(ShredAsync, () => ShredFiles.Count > 0 && !IsBusy);
        PickRootCommand = CreateCommand(PickRootAsync);
        FindLargeCommand = CreateCommand(FindLargeAsync);
        FindDuplicatesCommand = CreateCommand(FindDuplicatesAsync);
        DeleteSelectedCommand = CreateCommand(async p => await DeleteSelectedAsync(p as string ?? "large"));
        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is FileEntry entry) FileSystemHelper.OpenInExplorer(entry.FullPath);
            else if (p is DuplicateRow row) FileSystemHelper.OpenInExplorer(row.File.FullPath);
            else if (p is string path) FileSystemHelper.OpenInExplorer(path);
        });
        ShortcutCommand = new RelayCommand(p =>
        {
            if (p is string tool) ShellHelper.OpenSystemTool(tool);
        });
    }

    public event Action? ResultsChanged;

    public ICommand PickFilesCommand { get; }
    public ICommand ShredCommand { get; }
    public ICommand PickRootCommand { get; }
    public ICommand FindLargeCommand { get; }
    public ICommand FindDuplicatesCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ShortcutCommand { get; }

    public IReadOnlyList<string> ShredFiles { get => _shredFiles; private set => Set(ref _shredFiles, value); }
    public string ShredFileList { get => _shredFileList; private set => Set(ref _shredFileList, value); }
    public int Passes { get => _passes; set => Set(ref _passes, Math.Clamp(value, 1, 7)); }
    public string RootPath { get => _rootPath; set => Set(ref _rootPath, value); }
    public int MinSizeMb { get => _minSizeMb; set => Set(ref _minSizeMb, Math.Clamp(value, 1, 100_000)); }
    public IReadOnlyList<FileEntry> LargeFiles { get => _largeFiles; private set => Set(ref _largeFiles, value); }
    public IReadOnlyList<DuplicateGroup> DuplicateGroups { get => _duplicateGroups; private set => Set(ref _duplicateGroups, value); }
    public IReadOnlyList<DuplicateRow> DuplicateRows { get => _duplicateRows; private set => Set(ref _duplicateRows, value); }
    public string WastedText { get => _wastedText; private set => Set(ref _wastedText, value); }
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private async Task PickFilesAsync()
    {
        var files = await DialogService.PickFilesAsync(L("Toolbox_SelectFiles"));
        if (files.Count == 0) return;
        ShredFiles = files;
        ShredFileList = string.Join(Environment.NewLine, files);
        StatusMessage = LF("Common_Selected") + " " + files.Count;
    }

    private async Task ShredAsync()
    {
        if (ShredFiles.Count == 0) return;
        var detail = string.Join(Environment.NewLine, ShredFiles);
        var confirmed = await DialogService.ConfirmHighRiskAsync(
            L("Toolbox_Shred") + $" ({ShredFiles.Count})", detail, L("Toolbox_ShredHint"));
        if (!confirmed) return;

        await RunBusyAsync(async () =>
        {
            var progress = new Progress<ShredProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressText = $"{p.Processed}/{p.Total}  {p.CurrentFile}";
            });
            var count = await Services.Toolbox.ShredAsync(ShredFiles,
                new ShredOptions { Passes = Passes, RenameBeforeDelete = true }, progress);
            StatusMessage = $"{L("Status_Done")} {count}/{ShredFiles.Count}";
            ShredFiles = Array.Empty<string>();
            ShredFileList = string.Empty;
            ResultsChanged?.Invoke();
        }, L("Toolbox_Shred"));
    }

    private async Task PickRootAsync()
    {
        var folder = await DialogService.PickFolderAsync(L("Toolbox_RootPath"));
        if (!string.IsNullOrWhiteSpace(folder)) RootPath = folder;
    }

    private async Task FindLargeAsync()
    {
        await RunBusyAsync(async () =>
        {
            var progress = new Progress<string>(text => ProgressText = text);
            LargeFiles = await Services.Toolbox.FindLargeFilesAsync(new LargeFileScanOptions
            {
                RootPath = RootPath,
                MinimumSizeBytes = MinSizeMb * 1024L * 1024
            }, progress);
            StatusMessage = $"{L("Status_Done")} {LargeFiles.Count}";
            ResultsChanged?.Invoke();
        }, L("Toolbox_FindLarge"));
    }

    private async Task FindDuplicatesAsync()
    {
        await RunBusyAsync(async () =>
        {
            var progress = new Progress<string>(text => ProgressText = text);
            DuplicateGroups = await Services.Toolbox.FindDuplicatesAsync(new DuplicateScanOptions
            {
                RootPath = RootPath,
                MinimumSizeBytes = MinSizeMb * 1024L * 1024
            }, progress);
            DuplicateRows = DuplicateGroups.SelectMany(g => g.Files.Select(f => new DuplicateRow
            {
                Hash = g.Hash,
                File = f
            })).ToArray();
            WastedText = L("Toolbox_Wasted") + ": " +
                         FormatHelper.FormatBytes(DuplicateGroups.Sum(g => g.WastedBytes));
            StatusMessage = $"{L("Status_Done")} {DuplicateGroups.Count}";
            ResultsChanged?.Invoke();
        }, L("Toolbox_FindDuplicates"));
    }

    private async Task DeleteSelectedAsync(string which)
    {
        var entries = which == "duplicate"
            ? DuplicateRows.Where(r => r.IsChecked).Select(r => r.File).ToArray()
            : LargeFiles.Where(f => f.IsChecked).ToArray();
        if (entries.Length == 0)
        {
            StatusMessage = L("Status_NoSelection");
            return;
        }

        // 重复文件：每组至少保留一份，防止用户全选导致整组被删光
        if (which == "duplicate")
        {
            var removable = new List<FileEntry>();
            var blocked = 0;
            foreach (var group in DuplicateGroups)
            {
                var selected = DuplicateRows.Where(r => r.Hash == group.Hash && r.IsChecked).Select(r => r.File).ToArray();
                if (selected.Length >= group.Files.Count)
                {
                    blocked++;
                    continue; // 整组被选中：跳过，保留原件
                }
                removable.AddRange(selected);
            }
            if (blocked > 0)
                StatusMessage = LF("Toolbox_GroupProtected", blocked);
            entries = removable.ToArray();
        }
        if (entries.Length == 0) return;

        var detail = string.Join(Environment.NewLine, entries.Select(e => e.FullPath));
        var confirmed = await DialogService.ConfirmHighRiskAsync(
            LF("Toolbox_ConfirmDelete", entries.Length), detail);
        if (!confirmed) return;

        var success = 0;
        foreach (var entry in entries)
        {
            var (ok, error, _) = FileSystemHelper.DeleteToRecycleBin(entry.FullPath);
            Services.OperationLog.Log(Models.Common.OperationType.File,
                "删除工具箱文件", entry.FullPath, ok, ok ? null : error);
            if (ok) success++;
        }
        StatusMessage = $"{L("Status_Done")} {success}/{entries.Length}";
        if (which == "duplicate") await FindDuplicatesAsync();
        else await FindLargeAsync();
    }
}
