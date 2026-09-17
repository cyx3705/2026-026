using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>排版面上的一个元素；组件自身负责呈现它的内容和描述。</summary>
/// <param name="Element">要摆的控件。</param>
/// <param name="MinWidth">声明的最窄宽度；为 null 时按内容量。</param>
/// <param name="Flex">本元素是不是这一行的可变宽度元素（<see cref="PanelRowMode.Flex"/> 行才看）。</param>
internal sealed record BoardCell(FrameworkElement Element, double? MinWidth, bool Flex);

/// <summary>排版面上的一行，与声明里的一行一一对应。</summary>
internal sealed class BoardRow
{
    public PanelRowMode Mode { get; init; } = PanelRowMode.Flex;

    public IReadOnlyList<BoardCell> Cells { get; init; } = [];
}

/// <summary>
/// 控制面板的排版面（REQ-UI-060）。
///
/// **行由声明给出，不由宽度算出来。** 1.9.2 之前这里是「统一列宽 + 按宽度换行」：
/// 所有元素被拉成同一个宽度，一行能放几个由可用宽度决定。结果是声明侧说不出
/// 「这三个一行、那一个自己一行」，而每个元素也只能是同一个宽度——
/// 一个按钮和一个输入框在版面上占一样宽，两边都不合适。
///
/// 现在的规则只有三条：
/// <list type="number">
///   <item><b>每个元素先有一个最窄宽度</b>。按钮与说明文字按字宽量出来，不必注册；
///         文本框量不出来（空框的内容宽度是 0），因此按声明的 <c>minWidth</c>，
///         没声明就按 <see cref="DefaultInputMinWidth"/>。</item>
///   <item><b>放得下就不换行</b>。一行的元素按最窄宽度加起来还装得进可用宽度时，
///         余量按这一行的模式分掉：<see cref="PanelRowMode.Even"/> 等比放大，
///         <see cref="PanelRowMode.Flex"/> 全给可变的那一个。</item>
///   <item><b>放不下才折行</b>，而折出来的仍然是**同一行**：折出的每个视觉行
///         各自按母行的模式分配余量，行与行之间那条横线不会因为折行多出一条。
///         折行是宽度不够时的应对，不是版面结构的变化——
///         让它变出一条分隔线，等于让窗口宽度去改声明。</item>
/// </list>
///
/// **没有滚动条，也不会有。** 面板就是一块面板：内容多了往下长，不是往里滚。
///
/// **分隔线是画出来的，不是子元素。** 它们的位置只有排完版才知道：做成子元素就得先
/// 假设一个位置，于是折行处会冒出一条贴着行首的竖线。这里在 <see cref="OnRender"/> 里
/// 按最终版面画：同一视觉行里相邻元素之间画竖线，声明行与声明行之间画横线。
/// 组件内部的描述文字不再作为额外排版元素参与分隔线计算。
///
/// **线是实色发丝线，与表格同一种**（1.23.1，REQ-UI-122）：竖线上下各缩一截、
/// 与表头列分隔一样；横线贯穿整行、与表格行线一样。此前是两端渐隐的遮罩线，
/// 且矩形坐标带 +0.5，1px 线被抗锯齿摊到两排像素上，发虚、深浅不一，用户判为「渐消线有 bug」。
/// </summary>
internal sealed class AuroraPanelBoard : Panel
{
    /// <summary>文本框没声明 <c>minWidth</c> 时的最窄宽度。</summary>
    public const double DefaultInputMinWidth = 120;

    /// <summary>
    /// 按内容量出来的最窄宽度上限。
    ///
    /// 一段长说明文字的「自然宽度」是不换行时的整行宽，可以是几百像素；
    /// 拿它当最窄宽度，会让整行判成「放不下」而折行，而它本来是可以换行的。
    /// 封顶之后，长文字自己换行，别的元素不受它连累。
    /// </summary>
    private const double NaturalMinWidthCap = 260;

    /// <summary>同一视觉行里相邻元素的间距，竖分隔线画在正中。</summary>
    private const double ColumnGap = 12;

    /// <summary>声明行之间的间距，横分隔线画在正中。</summary>
    private const double RowGap = 8;

    /// <summary>同一声明行折出的视觉行之间的间距。**明显小于 <see cref="RowGap"/>**：
    /// 它们是一行折出来的，看上去必须比「两行」更紧。</summary>
    private const double LineGap = 3;

    /// <summary>竖分隔线上下各缩进多少，与表头列分隔线（<c>Margin="0,6"</c>）同值。</summary>
    private const double VerticalInset = 6;

    private readonly List<RowLayout> _layout = [];

    private IReadOnlyList<BoardRow> _rows = [];

    /// <summary>换一份行声明。子元素随之重挂——排版面不持有上一版的控件。</summary>
    public void SetRows(IReadOnlyList<BoardRow>? rows)
    {
        _rows = rows ?? [];
        InternalChildren.Clear();
        foreach (var row in _rows)
        {
            foreach (var cell in row.Cells)
                InternalChildren.Add(cell.Element);
        }

        InvalidateMeasure();
    }

    /// <summary>排完版后每个声明行折成了几个视觉行。门禁用：折行不改变行数这条要能断言。</summary>
    internal IReadOnlyList<int> LineCounts => _layout.Select(row => row.Lines.Count).ToList();

    /// <summary>
    /// 排完版后每个元素占的那一格，按【声明行 → 视觉行 → 元素】分组，坐标相对排版面。
    ///
    /// **门禁用的唯一introspection入口**。均布、可变、折行这三条规则的效果全都是
    /// 「谁多宽、在哪儿」，把它们做成一个个单独的只读属性，会让每加一条规则就多一个口子；
    /// 一份版面快照够所有断言用。
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<IReadOnlyList<Rect>>> CellBounds
        => _layout
            .Select(row => (IReadOnlyList<IReadOnlyList<Rect>>)row.Lines
                .Select(line => (IReadOnlyList<Rect>)line.Items
                    .Select(item => new Rect(item.X, row.Top + line.Top, item.Width, line.Height))
                    .ToList())
                .ToList())
            .ToList();

    /// <summary>上一次排版用的可用宽度。Arrange 拿到不一样的宽度时要重排。</summary>
    private double _laidOutFor = double.NaN;

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? double.PositiveInfinity
            : availableSize.Width;

        var height = Layout(limit);

        // **可用宽度有限时就要多宽报多宽**，即使内容按最窄宽度只占一半。
        // 报"内容宽"的话，父级会按那个宽度来 Arrange，于是可变元素永远吃不到余量——
        // 「有余量就放宽」这条规则会在版面上完全看不出来，而每一格的宽度都"没错"。
        return new Size(double.IsInfinity(limit) ? _widest : limit, height);
    }

    private double _widest;

    /// <summary>
    /// 按给定可用宽度排一次版，返回总高度。
    /// Measure 与 Arrange 共用同一段：两处各排一次的话，
    /// 「量的时候是两行、摆的时候是一行」这种错会以"最后一个元素跑到别处"的形态出现。
    /// </summary>
    private double Layout(double limit)
    {
        _layout.Clear();
        _widest = 0;
        _laidOutFor = limit;
        if (_rows.Count == 0)
            return 0;

        var y = 0d;
        for (var index = 0; index < _rows.Count; index++)
        {
            if (index > 0)
                y += RowGap;

            var layout = MeasureRow(_rows[index], limit);
            layout.Top = y;
            y += layout.Height;
            _widest = Math.Max(_widest, layout.Width);
            _layout.Add(layout);
        }

        return y;
    }

    private RowLayout MeasureRow(BoardRow row, double limit)
    {
        var layout = new RowLayout();
        if (row.Cells.Count == 0)
            return layout;

        // 一轮量取只为拿到「不受约束时它想要多宽」。第二轮按分到的宽度重量，
        // 会换行的内容（长文字）在那一步才把高度算对。
        var mins = new double[row.Cells.Count];
        for (var i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            if (cell.MinWidth is { } declared)
            {
                mins[i] = declared;
                continue;
            }

            cell.Element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            mins[i] = Math.Min(cell.Element.DesiredSize.Width, NaturalMinWidthCap);
        }

        var top = 0d;
        foreach (var line in Wrap(row.Cells, mins, limit))
        {
            if (layout.Lines.Count > 0)
                top += LineGap;

            var widths = Distribute(row, line, mins, limit);

            // 高度必须在**分完宽度之后**才量：会换行的内容（长说明文字）
            // 只有知道自己能占多宽，才算得出要占几行高。
            var height = 0d;
            for (var i = 0; i < line.Count; i++)
            {
                var cell = row.Cells[line[i]];
                cell.Element.Measure(new Size(widths[i], double.PositiveInfinity));
                height = Math.Max(height, cell.Element.DesiredSize.Height);
            }

            var placed = new Line { Top = top, Height = height };
            var x = 0d;
            for (var i = 0; i < line.Count; i++)
            {
                var cell = row.Cells[line[i]];
                placed.Items.Add(new Placed
                {
                    Element = cell.Element,
                    X = x,
                    Width = widths[i],
                });
                x += widths[i] + ColumnGap;
            }

            placed.Width = Math.Max(0, x - ColumnGap);
            layout.Lines.Add(placed);
            layout.Width = Math.Max(layout.Width, placed.Width);
            top += height;
        }

        layout.Height = top;
        return layout;
    }

    /// <summary>
    /// 折行：按最窄宽度贪心装满。**一行至少一个元素**——再窄也不能排成零个，
    /// 那样会进死循环，而症状是界面直接卡住。
    /// </summary>
    private static List<List<int>> Wrap(IReadOnlyList<BoardCell> cells, double[] mins, double limit)
    {
        var lines = new List<List<int>>();
        var current = new List<int>();
        var used = 0d;

        for (var i = 0; i < cells.Count; i++)
        {
            var next = used + (current.Count > 0 ? ColumnGap : 0) + mins[i];
            if (current.Count > 0 && !double.IsInfinity(limit) && next > limit)
            {
                lines.Add(current);
                current = [];
                used = 0;
                next = mins[i];
            }

            current.Add(i);
            used = next;
        }

        if (current.Count > 0)
            lines.Add(current);
        return lines;
    }

    /// <summary>
    /// 一个视觉行里的宽度怎么分。折出来的行**按母行的模式分**——
    /// 折行是宽度不够时的应对，不该顺带把这一行的分配方式也换掉。
    /// </summary>
    private static double[] Distribute(BoardRow row, List<int> line, double[] mins, double limit)
    {
        var count = line.Count;
        var widths = new double[count];
        var gaps = ColumnGap * (count - 1);
        var sum = 0d;
        for (var i = 0; i < count; i++)
        {
            widths[i] = mins[line[i]];
            sum += widths[i];
        }

        // 宽度无限（放进横向 StackPanel、或第一轮量取）时按最窄宽度返回：
        // 那种容器给不出可用宽度，「余量」无从谈起。
        if (double.IsInfinity(limit))
            return widths;

        var content = limit - gaps;
        if (content <= 0 || sum <= 0)
            return widths;

        // 装不下的只有一种情况：这一行只有一个元素，而它比整块面板还宽。
        // 让它缩到可用宽度，不让它顶出去——横向溢出是这块面板不接受的形态。
        if (content <= sum)
        {
            if (count == 1)
                widths[0] = content;
            return widths;
        }

        if (row.Mode == PanelRowMode.Even)
        {
            // 等比放大：宽的还是宽、窄的还是窄，一起长。
            // 末位吃余数，否则各自乘完再相加会在右边剩下一条取整误差造成的缝。
            var used = 0d;
            for (var i = 0; i < count - 1; i++)
            {
                widths[i] = Math.Round(content * widths[i] / sum);
                used += widths[i];
            }

            widths[count - 1] = content - used;
            return widths;
        }

        // 可变宽度：谁声明了 flex 就是谁，一个都没声明时是最右边那个。
        // 声明了两个只认第一个——「两个都变宽」没有一种分法说得出道理，
        // 而为此作废整份声明又太重。
        var target = count - 1;
        for (var i = 0; i < count; i++)
        {
            if (!row.Cells[line[i]].Flex)
                continue;
            target = i;
            break;
        }

        widths[target] += content - sum;
        return widths;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 父级给的最终宽度与量取时的可用宽度不一定相同（拉伸、滚动视口、四舍五入）。
        // 不重排的话，元素会按上一个宽度摆放，表现为右边空一条或最后一格被切掉。
        if (double.IsNaN(_laidOutFor) || Math.Abs(finalSize.Width - _laidOutFor) > 0.5)
            Layout(finalSize.Width <= 0 ? double.PositiveInfinity : finalSize.Width);

        foreach (var row in _layout)
        {
            foreach (var line in row.Lines)
            {
                foreach (var item in line.Items)
                    item.Element.Arrange(new Rect(item.X, row.Top + line.Top, item.Width, line.Height));
            }
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        // 令牌取不到时用一个可见的兜底色，而不是一根都不画。
        // 「线没了」在界面上看不出是"没做"还是"画刷没解析到"，那正是本轮要消灭的形态。
        var hairline = TryFindResource("Aurora.Brush.Hairline") as Brush ?? FallbackHairline;

        foreach (var row in _layout)
        {
            foreach (var line in row.Lines)
            {
                var inset = Math.Min(VerticalInset, line.Height / 4);
                var height = Math.Max(0, line.Height - (inset * 2));
                for (var index = 0; index < line.Items.Count - 1; index++)
                {
                    var item = line.Items[index];
                    var x = Math.Round(item.X + item.Width + (ColumnGap / 2));
                    var top = Math.Round(row.Top + line.Top + inset);
                    DrawHairline(drawingContext, hairline, new Rect(x, top, 1, Math.Round(height)));
                }
            }
        }

        // 横线只画在**声明行**之间。折出来的视觉行之间不画——那会让「一行折成两截」
        // 看上去变成「两行」，而声明里它就是一行。
        for (var index = 0; index < _layout.Count - 1; index++)
        {
            var row = _layout[index];
            var y = Math.Round(row.Top + row.Height + (RowGap / 2));
            DrawHairline(drawingContext, hairline, new Rect(0, y, Math.Max(0, ActualWidth), 1));
        }
    }

    /// <summary>
    /// 画一根 1px 实线。整数坐标 + 参考线让它正好落在一排设备像素上——
    /// 表格的行线靠 <c>SnapsToDevicePixels</c> 做到这一点，而那个属性管不到 OnRender 里画的东西。
    /// 不对齐的话，1px 线会被抗锯齿摊成两根半透明的线，看上去发虚、比表格浅。
    /// </summary>
    private static void DrawHairline(DrawingContext drawingContext, Brush brush, Rect rect)
    {
        var guidelines = new GuidelineSet(
            [rect.Left, rect.Right],
            [rect.Top, rect.Bottom]);
        drawingContext.PushGuidelineSet(guidelines);
        drawingContext.DrawRectangle(brush, null, rect);
        drawingContext.Pop();
    }

    private static readonly Brush FallbackHairline = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEB)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class Placed
    {
        public required UIElement Element { get; init; }

        public double X { get; init; }

        public double Width { get; init; }
    }

    private sealed class Line
    {
        public List<Placed> Items { get; } = [];

        /// <summary>相对所属声明行顶端的偏移。</summary>
        public double Top { get; init; }

        public double Height { get; init; }

        public double Width { get; set; }
    }

    private sealed class RowLayout
    {
        public List<Line> Lines { get; } = [];

        public double Top { get; set; }

        public double Height { get; set; }

        public double Width { get; set; }
    }
}
