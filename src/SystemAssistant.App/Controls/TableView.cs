using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Windows.Input;
using SystemAssistant.App.Mvvm;
using SystemAssistant.App.Services;

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
/// 支持可选复选列、单击选中、双击打开详情、表头点击排序。
/// </summary>
public sealed class TableView : UserControl
{
    private readonly Grid _header = new() { Height = 32 };
    private readonly ItemsControl _rows = new();
    private readonly ScrollViewer _scroll = new();
    private IReadOnlyList<TableColumn> _columns = Array.Empty<TableColumn>();
    private bool _withCheckBox;
    private Func<object, bool>? _getChecked;
    private Action<object, bool>? _setChecked;
    private Border? _selectedBorder;
    private object? _selectedItem;
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(45, 0, 120, 215));

    public TableView()
    {
        _scroll.Content = _rows;
        _scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        var root = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        root.Children.Add(_header);
        root.Children.Add(_scroll);
        Content = root;
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
            var headerButton = new Button
            {
                Classes = { "tableHeader" },
                Content = LocalizationService.Instance.Get(column.HeaderKey),
                Command = column.SortKey is null ? null : new RelayCommand(() => SortRequested?.Invoke(column.SortKey!))
            };
            // 表头文本随语言即时更新
            LocalizationService.Instance.LanguageChanged += (_, _) =>
                headerButton.Content = LocalizationService.Instance.Get(column.HeaderKey);
            Grid.SetColumn(headerButton, columnIndex++);
            _header.Children.Add(headerButton);
        }
    }

    /// <summary>设置数据源并重建行。</summary>
    public void SetItems(System.Collections.IEnumerable items)
    {
        _selectedItem = null;
        _selectedBorder = null;
        var list = items.Cast<object?>().Where(i => i is not null).Select(i => i!).ToList();
        _rows.ItemTemplate = new FuncDataTemplate<object>((item, _) => BuildRow(item), true);
        _rows.ItemsSource = list;
    }

    /// <summary>复选状态变更后重建行（不重新绑定数据源，保留滚动位置）。</summary>
    public void Refresh()
    {
        if (_rows.ItemsSource is IEnumerable<object> list) SetItems(list.ToList());
    }

    /// <summary>当前表格中所有数据对象。</summary>
    public IEnumerable<object> EnumerateItems()
        => _rows.ItemsSource is IEnumerable<object> list ? list : Enumerable.Empty<object>();

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

    private Control BuildRow(object item)
    {
        var grid = new Grid { ColumnDefinitions = BuildColumnDefinitions() };
        var index = 0;
        if (_withCheckBox)
        {
            var check = new CheckBox
            {
                IsChecked = _getChecked?.Invoke(item) ?? false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked is bool value)
                {
                    _setChecked?.Invoke(item, value);
                    CheckChanged?.Invoke();
                }
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);
            index++;
        }

        foreach (var column in _columns)
        {
            string text;
            try { text = column.GetValue(item) ?? string.Empty; }
            catch { text = string.Empty; }
            var cell = new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12
            };
            Grid.SetColumn(cell, index++);
            grid.Children.Add(cell);
        }

        var border = new Border { Child = grid, Background = Brushes.Transparent, Tag = item };
        border.PointerPressed += (_, _) =>
        {
            if (_selectedBorder is not null) _selectedBorder.Background = Brushes.Transparent;
            _selectedBorder = border;
            _selectedItem = item;
            border.Background = SelectionBrush;
            SelectionChanged?.Invoke(item);
        };
        border.DoubleTapped += (_, _) => RowDoubleTapped?.Invoke(item);
        return border;
    }
}
