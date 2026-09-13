using System.Windows;
using System.Windows.Controls;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>
/// 响应式栅格（REQ-UI-015）：按可用宽度决定放几列，放不下就自动少一列。
///
/// 为什么要有它：此前只有 <c>stack</c>，列宽写死。窗口窄到 720px 以下时旧页面会收起
/// 权重/点击/最近打开三列，而描述化之后这条做不到，只能横向溢出
/// （见 b-Office/history/前端组件缺失需求书 C5）。
///
/// 列数**不接受声明**，只接受"一列至少多宽"。声明列数的话，窄窗口下的溢出会原样回来，
/// 而那正是这个组件要解决的问题。
/// </summary>
internal sealed class AuroraGridPanel : Panel
{
    internal static readonly DependencyProperty FillHeightProperty = DependencyProperty.RegisterAttached(
        "FillHeight", typeof(bool), typeof(AuroraGridPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    private double[] _naturalHeights = [];
    private bool[] _fillRows = [];

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(AuroraGridPanel),
        new FrameworkPropertyMetadata(240d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(AuroraGridPanel),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>一列的下限宽度。可用宽度不够两列时就只排一列。</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>列与行之间的间距。</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>当前列数，供测试断言"窄了就收列"这条行为。</summary>
    public int Columns { get; private set; } = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
        {
            Columns = 1;
            return new Size(0, 0);
        }

        var gap = Math.Max(0, Gap);
        var min = Math.Max(1, MinColumnWidth);

        // 宽度无限（放进横向 StackPanel 或 ScrollViewer）时排成一行：
        // 那种容器不会给出可用宽度，按 1 列排会把内容挤成一条。
        var columns = double.IsInfinity(availableSize.Width)
            ? children.Count
            : (int)Math.Floor((availableSize.Width + gap) / (min + gap));
        Columns = Math.Clamp(columns, 1, children.Count);

        var cell = double.IsInfinity(availableSize.Width)
            ? min
            : Math.Max(min, (availableSize.Width - (gap * (Columns - 1))) / Columns);

        var rows = (int)Math.Ceiling(children.Count / (double)Columns);
        var heights = new double[rows];
        _fillRows = new bool[rows];
        for (var i = 0; i < children.Count; i++)
            _fillRows[i / Columns] |= (bool)children[i].GetValue(FillHeightProperty);
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if ((bool)child.GetValue(FillHeightProperty)) continue;
            child.Measure(new Size(cell, double.PositiveInfinity));
            var row = i / Columns;
            heights[row] = Math.Max(heights[row], child.DesiredSize.Height);
        }

        _naturalHeights = heights;
        heights = AllocateHeights(availableSize.Height, gap);
        for (var i = 0; i < children.Count; i++)
        {
            if (!(bool)children[i].GetValue(FillHeightProperty)) continue;
            children[i].Measure(new Size(cell, double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : heights[i / Columns]));
            if (double.IsInfinity(availableSize.Height))
                heights[i / Columns] = Math.Max(heights[i / Columns], children[i].DesiredSize.Height);
        }

        var height = heights.Sum() + (gap * (rows - 1));
        var width = (cell * Columns) + (gap * (Columns - 1));
        return new Size(
            double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width),
            Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        if (children.Count == 0)
            return finalSize;

        var gap = Math.Max(0, Gap);
        var cell = Math.Max(1, (finalSize.Width - (gap * (Columns - 1))) / Columns);
        var rows = (int)Math.Ceiling(children.Count / (double)Columns);

        var heights = AllocateHeights(finalSize.Height, gap);

        var y = 0d;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var index = (row * Columns) + column;
                if (index >= children.Count)
                    break;
                children[index].Arrange(new Rect(
                    column * (cell + gap),
                    y,
                    cell,
                    heights[row]));
            }

            y += heights[row] + gap;
        }

        return finalSize;
    }

    private double[] AllocateHeights(double available, double gap)
    {
        var heights = (double[])_naturalHeights.Clone();
        var count = _fillRows.Count(fill => fill);
        if (count == 0 || double.IsInfinity(available)) return heights;
        var fixedHeight = heights.Where((_, i) => !_fillRows[i]).Sum();
        var share = Math.Max(0, available - fixedHeight - gap * Math.Max(0, heights.Length - 1)) / count;
        for (var i = 0; i < heights.Length; i++)
            if (_fillRows[i]) heights[i] = share;
        return heights;
    }
}
