using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace SystemAssistant.App.Controls;

/// <summary>
/// 轻量实时折线图控件（自绘，不依赖第三方绘图包）。
/// 说明：OxyPlot.Avalonia 2.1 仅支持 Avalonia 0.10，2.2 需要 .NET 10 / Avalonia 12，
/// 与本项目 .NET 9 + Avalonia 11 不兼容，因此这里用 Avalonia 原生 DrawingContext 实现。
/// </summary>
public sealed class LineChartControl : Control
{
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private IBrush _stroke = Brushes.DodgerBlue;

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<LineChartControl, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<LineChartControl, string>(nameof(Unit), string.Empty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public LineChartControl()
    {
        MinHeight = 110;
        Height = 130;
        TitleProperty.Changed.AddClassHandler<LineChartControl>((_, _) => InvalidateVisual());
    }

    /// <summary>更新曲线数据并触发重绘。</summary>
    public void SetSeries(IReadOnlyList<double> values, string title, string unit, IBrush? stroke = null)
    {
        _values = values ?? Array.Empty<double>();
        Title = title;
        Unit = unit;
        if (stroke is not null) _stroke = stroke;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 10 || height <= 10) return;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var textBrush = new SolidColorBrush(isDark ? Color.FromRgb(230, 230, 230) : Color.FromRgb(40, 45, 50));
        var gridBrush = new SolidColorBrush(isDark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(45, 0, 0, 0));
        var gridPen = new Pen(gridBrush, 1);

        const double left = 8, right = 8, top = 26, bottom = 18;
        var chartWidth = Math.Max(1, width - left - right);
        var chartHeight = Math.Max(1, height - top - bottom);

        // 水平网格线
        for (var i = 0; i <= 3; i++)
        {
            var y = top + chartHeight * i / 3.0;
            context.DrawLine(gridPen, new Point(left, y), new Point(width - right, y));
        }

        // 标题与当前值
        var current = _values.Count > 0 ? _values[^1].ToString("F1", CultureInfo.CurrentCulture) : "--";
        DrawText(context, $"{Title}   {current} {Unit}".Trim(), new Point(left, 5), textBrush, 11, true);

        if (_values.Count < 2) return;

        var min = _values.Min();
        var max = _values.Max();
        if (Math.Abs(max - min) < 0.0001)
        {
            max = min + 1;
            min -= 1;
        }
        else
        {
            var padding = (max - min) * 0.15;
            max += padding;
            min -= padding;
        }

        var strokePen = new Pen(_stroke, 2);
        var points = new Point[_values.Count];
        for (var i = 0; i < _values.Count; i++)
        {
            var x = left + chartWidth * i / Math.Max(1, _values.Count - 1);
            var normalized = (_values[i] - min) / (max - min);
            var y = top + chartHeight * (1 - normalized);
            points[i] = new Point(x, y);
        }
        for (var i = 1; i < points.Length; i++)
            context.DrawLine(strokePen, points[i - 1], points[i]);

        // 末尾高亮圆点
        context.DrawEllipse(_stroke, null, points[^1], 3, 3);

        DrawText(context, max.ToString("F0", CultureInfo.CurrentCulture), new Point(left, top - 2), textBrush, 9);
        DrawText(context, min.ToString("F0", CultureInfo.CurrentCulture),
            new Point(left, top + chartHeight - 12), textBrush, 9);
    }

    private static void DrawText(DrawingContext context, string text, Point origin,
        IBrush brush, double size, bool bold = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, bold ? FontStyle.Normal : FontStyle.Normal,
                bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);
        context.DrawText(formatted, origin);
    }
}
