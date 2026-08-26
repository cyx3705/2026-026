using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HistoryAurora.Shell.Widgets;

/// <summary>
/// 控制面板的排版面。控件按声明顺序铺开，一行放不下就换行——因此它天然是**多行多列**，
/// 而不是「要么一行、要么一列」。
///
/// **没有滚动条，也不会有。** 面板就是一块面板：内容多了往下长，不是往里滚。
/// 宽度不够时靠换行解决，所以横向永远不会溢出；纵向由行数决定，交给外层布局。
///
/// **分隔线是画出来的，不是子元素。** 它们的位置只有排完版才知道：做成子元素就得先
/// 假设一个位置，于是换行处会冒出一条贴着行首的竖线。这里在 <see cref="OnRender"/> 里
/// 按最终行列画：同一行的相邻控件之间画竖线，行与行之间画横线，两端渐隐。
/// </summary>
internal sealed class AuroraPanelBoard : Panel
{
    /// <summary>列宽下限。太窄的列会把标签和输入框挤成两截。</summary>
    public double MinItemWidth { get; set; } = 112;

    /// <summary>列宽上限。单个控件霸占整行会让面板看上去像个表单。</summary>
    public double MaxItemWidth { get; set; } = 260;

    /// <summary>同一行里相邻控件的间距，分隔线画在正中。</summary>
    public double ColumnGap { get; set; } = 12;

    /// <summary>行距，同上。</summary>
    public double RowGap { get; set; } = 8;

    private readonly List<Row> _rows = [];

    private double _columnWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        _rows.Clear();
        _columnWidth = 0;
        if (InternalChildren.Count == 0)
            return new Size(0, 0);

        // 列宽统一。让每个控件各占各的宽度会排出参差不齐的锯齿边，
        // 「多行多列」看上去就只是「换了行的一排」;取最宽的那个当列宽,
        // 再夹在上下限之间——过窄会把标签和输入框挤成两截,过宽会让一个控件霸占整行。
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(MaxItemWidth, double.PositiveInfinity));
            _columnWidth = Math.Max(_columnWidth, child.DesiredSize.Width);
        }

        _columnWidth = Math.Clamp(_columnWidth, MinItemWidth, MaxItemWidth);

        var limit = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? double.PositiveInfinity
            : availableSize.Width;

        // 一行放得下几列。至少一列——再窄也不能排成零列。
        var columns = double.IsInfinity(limit)
            ? InternalChildren.Count
            : Math.Max(1, (int)Math.Floor((limit + ColumnGap) / (_columnWidth + ColumnGap)));

        var row = new Row();
        foreach (UIElement child in InternalChildren)
        {
            if (row.Items.Count == columns)
            {
                _rows.Add(row);
                row = new Row();
            }

            // 二次量取用的是最终列宽：控件在这一步才知道自己实际能占多宽，
            // 会换行的内容（长文字）也在这一步把高度算对。
            child.Measure(new Size(_columnWidth, double.PositiveInfinity));
            row.Items.Add(child);
            row.Height = Math.Max(row.Height, child.DesiredSize.Height);
        }

        if (row.Items.Count > 0)
            _rows.Add(row);

        var usedColumns = _rows.Count == 0 ? 0 : _rows.Max(r => r.Items.Count);
        var width = (usedColumns * _columnWidth) + (Math.Max(0, usedColumns - 1) * ColumnGap);
        var height = _rows.Sum(r => r.Height) + (Math.Max(0, _rows.Count - 1) * RowGap);
        return new Size(double.IsInfinity(limit) ? width : Math.Min(width, limit), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var row in _rows)
        {
            var x = 0d;
            foreach (var item in row.Items)
            {
                item.Arrange(new Rect(x, y, _columnWidth, row.Height));
                x += _columnWidth + ColumnGap;
            }

            row.Top = y;
            y += row.Height + RowGap;
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        // 令牌取不到时用一个可见的兜底色，而不是一根都不画。
        // 「线没了」在界面上看不出是"没做"还是"画刷没解析到"，那正是本轮要消灭的形态。
        var hairline = TryFindResource("Aurora.Brush.Hairline") as Brush ?? FallbackHairline;

        foreach (var row in _rows)
        {
            var inset = Math.Min(7, row.Height / 4);
            var lineHeight = Math.Max(0, row.Height - (inset * 2));
            for (var index = 0; index < row.Items.Count - 1; index++)
            {
                var x = Math.Round(((index + 1) * (_columnWidth + ColumnGap)) - (ColumnGap / 2)) + 0.5;
                drawingContext.PushOpacityMask(VerticalFade);
                drawingContext.DrawRectangle(hairline, null, new Rect(x, row.Top + inset, 1, lineHeight));
                drawingContext.Pop();
            }
        }

        for (var index = 0; index < _rows.Count - 1; index++)
        {
            var row = _rows[index];
            var y = Math.Round(row.Top + row.Height + (RowGap / 2)) + 0.5;
            drawingContext.PushOpacityMask(HorizontalFade);
            drawingContext.DrawRectangle(hairline, null, new Rect(0, y, Math.Max(0, ActualWidth), 1));
            drawingContext.Pop();
        }
    }

    private static readonly Brush FallbackHairline = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEB)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>两端渐隐的遮罩。冻结后可复用，不必每根线新建一把画刷。</summary>
    private static readonly Brush VerticalFade = CreateFade(new Point(0, 0), new Point(0, 1));

    private static readonly Brush HorizontalFade = CreateFade(new Point(0, 0), new Point(1, 0));

    private static Brush CreateFade(Point start, Point end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
            GradientStops =
            [
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, 0.18),
                new GradientStop(Colors.Black, 0.82),
                new GradientStop(Colors.Transparent, 1),
            ],
        };
        brush.Freeze();
        return brush;
    }

    private sealed class Row
    {
        public List<UIElement> Items { get; } = [];

        public double Height { get; set; }

        public double Top { get; set; }
    }
}
