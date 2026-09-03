using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Components.Widgets;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 响应式栅格的契约（REQ-UI-015）。旧页面窄于 720px 时会收起三列；
/// 描述化之后只有 stack、列宽写死，窄窗口下就横向溢出（需求书 C5）。
///
/// 断言的是**列数由可用宽度决定**这一条，而不是某个具体像素值——
/// 断言像素会让这条门禁在换字号时就假失败。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class ResponsiveGridContractTests
{
    [Fact]
    public void Columns_ShrinkWithAvailableWidth()
    {
        UiTestHost.RunSta(() =>
        {
            var grid = Build(6, min: 200, gap: 10);

            grid.Measure(new Size(1000, double.PositiveInfinity));
            Assert.Equal(4, grid.Columns);

            grid.Measure(new Size(700, double.PositiveInfinity));
            Assert.Equal(3, grid.Columns);

            // 窄到放不下两列时收成一列，而不是溢出。
            grid.Measure(new Size(260, double.PositiveInfinity));
            Assert.Equal(1, grid.Columns);
        });
    }

    [Fact]
    public void Columns_NeverExceedTheChildCount()
    {
        UiTestHost.RunSta(() =>
        {
            var grid = Build(2, min: 100, gap: 0);

            grid.Measure(new Size(2000, double.PositiveInfinity));
            Assert.Equal(2, grid.Columns);
        });
    }

    [Fact]
    public void Arrange_StacksRowsWithoutOverlap()
    {
        UiTestHost.RunSta(() =>
        {
            var grid = Build(4, min: 200, gap: 10);
            grid.Measure(new Size(430, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, 430, grid.DesiredSize.Height));

            Assert.Equal(2, grid.Columns);
            var first = (FrameworkElement)grid.Children[0];
            var third = (FrameworkElement)grid.Children[2];

            // 第三个落到第二行：同一行内的两个不该叠在一起。
            var firstBottom = first.TranslatePoint(new Point(0, first.ActualHeight), grid).Y;
            var thirdTop = third.TranslatePoint(new Point(0, 0), grid).Y;
            Assert.True(thirdTop >= firstBottom, $"third top={thirdTop}, first bottom={firstBottom}");
        });
    }

    private static AuroraGridPanel Build(int children, double min, double gap)
    {
        var grid = new AuroraGridPanel { MinColumnWidth = min, Gap = gap };
        for (var i = 0; i < children; i++)
            grid.Children.Add(new Border { Height = 24 });
        return grid;
    }
}
