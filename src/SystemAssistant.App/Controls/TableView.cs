using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;
using SystemAssistant.Models.Config;

namespace SystemAssistant.App.Controls;

/// <summary>表格列定义。GetValue 把任意数据对象格式化为显示文本，SortKey 用于表头排序回调。</summary>
public sealed class TableColumn
{
    public required string HeaderKey { get; init; }
    public required Func<object, string> GetValue { get; init; }
    public double Width { get; init; } = 140;
    public bool Stretch { get; init; }
    public string? SortKey { get; init; }
}

/// <summary>
/// 通用可排序表格控件（代码构建，避免引入 DataGrid 包）。
/// 支持复选列、单击选中、双击打开详情、表头点击排序、斑马纹与悬停高亮。
/// 表头与数据行放在同一个滚动容器内，横向滚动时不会错位。
/// </summary>
public sealed class TableView : UserControl
{
    private sealed class RowView
    {
        public required Border Root { get; init; }
        public required object Item { get; init; }
        public required List<TextBlock> Cells { get; init; }
        public required int Index { get; init; }
        public CheckBox? Check { get; init; }
    }

    private readonly Grid _header = new() { Height = 34 };
    private readonly ItemsControl _rows = new();
    private readonly ScrollViewer _scroll = new();
    private readonly TextBlock _emptyHint = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 13,
        IsVisible = false
    };
    private readonly List<RowView> _rowViews = new();
    private IReadOnlyList<TableColumn> _columns = Array.Empty<TableColumn>();
    private bool _withCheckBox;
    private Func<object, bool>? _getChecked;
    private Action<object, bool>? _setChecked;
    private Border? _selectedBorder;
    private RowView? _selectedRow;
    private object? _selectedItem;
    private bool _suppressCheckEvents;
    private bool _languageHooked;

    public TableView()
    {
        _scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        // 空数据时给出占位提示，避免整张表只剩表头 + 大片空白。
        _emptyHint.Classes.Add("caption");
        Localize.SetKey(_emptyHint, "Common_Empty");
        var body = new Grid { MinHeight = 120 };
        body.Children.Add(_rows);
        body.Children.Add(_emptyHint);

        var inner = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        inner.Children.Add(_header);
        inner.Children.Add(body);
        _scroll.Content = inner;
        Content = _scroll;
        MinHeight = 140;
    }

    /// <summary>排序请求，参数为 TableColumn.SortKey。</summary>
    public event Action<string>? SortRequested;

    /// <summary>当前选中行。</summary>
    public object? SelectedItem => _selectedItem;

    public event Action<object?>? SelectionChanged;

    /// <summary>双击行（用于打开详情）。</summary>
    public event Action<object>? RowDoubleTapped;

    /// <summary>复选状态变化（用于刷新「已选数量」）。</summary>
    public event Action? CheckChanged;

    public void Configure(IReadOnlyList<TableColumn> columns, bool withCheckBox = false,
        Func<object, bool>? getChecked = null, Action<object, bool>? setChecked = null)
    {
        _columns = columns;
        _withCheckBox = withCheckBox;
        _getChecked = getChecked;
        _setChecked = setChecked;

        _header.ColumnDefinitions = BuildColumnDefinitions();
        _header.Children.Clear();

        var columnIndex = 0;
        if (withCheckBox)
        {
            var selectAll = new CheckBox { VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0) };
            selectAll.IsCheckedChanged += (_, _) =>
            {
                if (selectAll.IsChecked is not bool value) return;
                foreach (var item in EnumerateItems()) _setChecked?.Invoke(item, value);
                Refresh();
                CheckChanged?.Invoke();
            };
            Grid.SetColumn(selectAll, 0);
            _header.Children.Add(selectAll);
            columnIndex++;
        }

        foreach (var column in columns)
        {
            var sortKey = column.SortKey;
            var headerButton = new Button
            {
                Classes = { "tableHeader" },
                Command = sortKey is null ? null : new RelayCommand(() => SortRequested?.Invoke(sortKey))
            };
            // 交给 Loc.Key 统一管理：不再对单例订阅匿名 lambda，避免泄漏与陈旧表头。
            Localize.SetKey(headerButton, column.HeaderKey);
            Grid.SetColumn(headerButton, columnIndex++);
            _header.Children.Add(headerButton);
        }

        if (!_languageHooked)
        {
            _languageHooked = true;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
        }
    }

    /// <summary>设置数据源并重建行；同一对象仍存在于新快照时保留选中态。</summary>
    public void SetItems(System.Collections.IEnumerable items)
    {
        var previous = _selectedItem;
        var list = items.Cast<object?>().Where(i => i is not null).Select(i => i!).ToList();
        _rowViews.Clear();
        _selectedItem = previous is not null
            ? list.FirstOrDefault(x => ReferenceEquals(x, previous))
            : null;
        _selectedBorder = null;
        _selectedRow = null;
        _rows.ItemTemplate = new FuncDataTemplate<object>((item, _) => BuildRow(item), false);
        _rows.ItemsSource = list;
        _emptyHint.IsVisible = list.Count == 0;

        if (_selectedItem is not null)
        {
            var restored = _rowViews.FirstOrDefault(r => ReferenceEquals(r.Item, _selectedItem));
            if (restored is not null)
            {
                _selectedRow = restored;
                _selectedBorder = restored.Root;
                restored.Root.Background = ThemeService.Brush("SaSelectionBrush");
            }
        }
    }

    /// <summary>
    /// 复选状态 / 语言切换后就地刷新行文本与勾选框，不重建 ItemsSource，
    /// 因此滚动位置、选中行都不会丢失。
    /// </summary>
    public void Refresh()
    {
        _suppressCheckEvents = true;
        try
        {
            foreach (var row in _rowViews)
            {
                if (row.Check is not null)
                    row.Check.IsChecked = _getChecked?.Invoke(row.Item) ?? false;
                for (var i = 0; i < row.Cells.Count && i < _columns.Count; i++)
                {
                    try { row.Cells[i].Text = _columns[i].GetValue(row.Item) ?? string.Empty; }
                    catch { row.Cells[i].Text = string.Empty; }
                }
            }
        }
        finally { _suppressCheckEvents = false; }
    }

    /// <summary>当前表格中所有数据对象。</summary>
    public IEnumerable<object> EnumerateItems()
        => _rows.ItemsSource is IEnumerable<object> list ? list : Enumerable.Empty<object>();

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void OnThemeChanged(ThemeMode _)
    {
        // 调色板是共享画刷（原地改色），这里只需把选中 / 悬停态重新赋一次。
        foreach (var row in _rowViews)
        {
            if (ReferenceEquals(row, _selectedRow)) continue;
            row.Root.Background = RowBackground(row.Index);
        }
    }

    private ColumnDefinitions BuildColumnDefinitions()
    {
        var definitions = new ColumnDefinitions();
        if (_withCheckBox) definitions.Add(new ColumnDefinition(38, GridUnitType.Pixel));
        foreach (var column in _columns)
        {
            definitions.Add(column.Stretch
                ? new ColumnDefinition(1, GridUnitType.Star)
                : new ColumnDefinition(column.Width, GridUnitType.Pixel));
        }
        return definitions;
    }

    private IBrush RowBackground(int index)
        => index % 2 == 1 ? ThemeService.Brush("SaRowAltBrush") : Brushes.Transparent;

    private Control BuildRow(object item)
    {
        var index = _rowViews.Count;
        var grid = new Grid { ColumnDefinitions = BuildColumnDefinitions(), MinHeight = 30 };
        CheckBox? check = null;
        var columnIndex = 0;
        if (_withCheckBox)
        {
            check = new CheckBox
            {
                IsChecked = _getChecked?.Invoke(item) ?? false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            check.IsCheckedChanged += (_, _) =>
            {
                if (_suppressCheckEvents || check.IsChecked is not bool value) return;
                _setChecked?.Invoke(item, value);
                CheckChanged?.Invoke();
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);
            columnIndex++;
        }

        var cells = new List<TextBlock>(_columns.Count);
        foreach (var column in _columns)
        {
            string text;
            try { text = column.GetValue(item) ?? string.Empty; }
            catch { text = string.Empty; }
            var cell = new TextBlock
            {
                Text = text,
                Margin = new Thickness(8, 5),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12
            };
            cells.Add(cell);
            Grid.SetColumn(cell, columnIndex++);
            grid.Children.Add(cell);
        }

        var border = new Border
        {
            Child = grid,
            Background = RowBackground(index),
            Tag = item
        };
        var row = new RowView { Root = border, Item = item, Cells = cells, Index = index, Check = check };
        _rowViews.Add(row);

        border.PointerPressed += (_, _) =>
        {
            if (_selectedBorder is not null && _selectedRow is not null)
                _selectedBorder.Background = RowBackground(_selectedRow.Index);
            _selectedBorder = border;
            _selectedRow = row;
            _selectedItem = item;
            border.Background = ThemeService.Brush("SaSelectionBrush");
            SelectionChanged?.Invoke(item);
        };
        border.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(_selectedRow, row))
                border.Background = ThemeService.Brush("SaHoverBrush");
        };
        border.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(_selectedRow, row))
                border.Background = RowBackground(row.Index);
        };
        border.DoubleTapped += (_, _) => RowDoubleTapped?.Invoke(item);
        return border;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_languageHooked)
        {
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            _languageHooked = false;
        }
        base.OnDetachedFromVisualTree(e);
    }
}
