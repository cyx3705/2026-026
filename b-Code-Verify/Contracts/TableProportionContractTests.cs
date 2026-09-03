using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Graph;
using HistoryAurora.Shell.Components.Table;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 表格按页面宽度等比缩放（REQ-UI-039），以及泳道图默认停在分支头（REQ-UI-040）。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class TableProportionContractTests
{
    /// <summary>
    /// 列宽声明是**比例**不是像素：换宽度时各列同比缩放，且右边不留空扩展区。
    ///
    /// 判据取「各列宽度之和 == 视口宽度」。留一条几像素的缝在截图上看不出来，
    /// 但它正是"表格右边空了一块"的样子。
    /// </summary>
    [Theory]
    [InlineData(700)]
    [InlineData(1200)]
    public void ColumnsScaleWithTheViewportAndLeaveNoGapOnTheRight(double windowWidth)
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            var host = new Window
            {
                Content = table,
                Width = windowWidth,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };

            try
            {
                host.Show();
                table.SetData(AuroraTableData.FromItems(
                    Enumerable.Range(0, 30).Select(index => (Name: "row" + index, Text: new string('文', 30))),
                    ("名称", "150", row => row.Name),
                    ("类别", "70", _ => "模块"),
                    ("说明", AuroraTableColumn.Star, row => row.Text)));

                for (var pass = 0; pass < 6; pass++)
                {
                    host.UpdateLayout();
                    UiTestHost.Pump();
                }

                var scroll = Descendants<ScrollViewer>(table).First();
                var headers = Descendants<GridViewColumnHeader>(table)
                    .Where(header => header.Column != null)
                    .ToList();
                Assert.Equal(3, headers.Count);

                // 列宽合计 + 行自身的左右内距 = 视口。
                // 行样式 Aurora.Table.Row 的 Padding 是 8,0，左右各 8px——那是**对称的内距**，
                // 不是右侧那种可以被拖宽的空扩展区，所以它是允许剩下的唯一一份宽度。
                const double RowInset = 16;
                var sum = headers.Sum(header => header.Column!.ActualWidth);
                var unused = scroll.ViewportWidth - sum;
                Assert.InRange(unused, 0, RowInset + 1);

                // 反过来也要钉住：不许溢出。
                Assert.All(
                    Descendants<ScrollViewer>(table),
                    viewer => Assert.True(
                        viewer.ExtentWidth <= viewer.ViewportWidth + 1,
                        $"内容宽 {viewer.ExtentWidth} 超出视口 {viewer.ViewportWidth}"));

                // 150 : 70 的比例必须守住，不能因为缩放就变成均分。
                var name = headers.Single(h => Equals(h.Content, "名称")).Column!.ActualWidth;
                var kind = headers.Single(h => Equals(h.Content, "类别")).Column!.ActualWidth;
                Assert.InRange(name / kind, 150d / 70 * 0.9, 150d / 70 * 1.1);
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>
    /// 泳道图打开就停在最右端：分支头（各泳道最新节点）在右边缘，
    /// 停在左端等于每次都要先手动拖一段。
    /// </summary>
    [Fact]
    public void SwimlaneStartsAtTheBranchHeadsInsteadOfTheLeftEdge()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new HistoryAurora.Shell.Neutral.Logging.MemoryShellLog();
            var bus = new CommandBus(registry, log);
            var swimlane = new AuroraSwimlane(bus, log, new ActionRegistry(bus, log));

            var host = new Window
            {
                Content = swimlane,
                Width = 420,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };

            try
            {
                host.Show();
                UiTestHost.Pump();

                // 节点要多到一屏放不下，否则"停在最右"和"停在最左"是同一个位置。
                var nodes = string.Join(",", Enumerable.Range(1, 40).Select(index =>
                    index == 1
                        ? """{ "id": "n1", "title": "n1", "parents": [] }"""
                        : $$"""{ "id": "n{{index}}", "title": "n{{index}}", "parents": [ "n{{index - 1}}" ] }"""));

                var parsed = SwimlaneReader.Read($$"""
                    { "schemaVersion": 1, "title": "长链", "nodes": [ {{nodes}} ] }
                    """);
                Assert.True(parsed.Ok, parsed.Error);
                swimlane.SetDescription(parsed.Value);

                for (var pass = 0; pass < 6; pass++)
                {
                    host.UpdateLayout();
                    UiTestHost.Pump();
                }

                var viewport = Descendants<ScrollViewer>(swimlane).First();
                Assert.True(viewport.ScrollableWidth > 0, "图没有宽到需要滚动，这一条断言不成立");
                Assert.True(
                    Math.Abs(viewport.HorizontalOffset - viewport.ScrollableWidth) <= 1,
                    $"打开时停在 {viewport.HorizontalOffset}，最右端是 {viewport.ScrollableWidth}");
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
