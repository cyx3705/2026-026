using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 星号列必须把表格**恰好**填满，不能溢出成一条横向滚动条。
///
/// 1.7.0 真机上命令集那张表就长了一条：定宽列的**实际**宽度不等于声明值——
/// 列宽声明小于表头文字所需时 GridView 会把那一列撑开，几列各多两三像素就够了。
/// 星号列是按声明值算的，于是总和超出可视区。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class TableWidthContractTests
{
    [Fact]
    public void StarColumnDoesNotOverflowWhenFixedColumnsAreWidenedByTheirHeaders()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            var host = new Window
            {
                Content = table,
                Width = 600,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };

            try
            {
                host.Show();
                UiTestHost.Pump();

                // 与真机命令集同一套列：这条门禁要复现的是那张表长出的横向滚动条。
                var rows = Enumerable.Range(0, 60)
                    .Select(index => (Name: "aurora.log.command" + index, Text: new string('说', 40)))
                    .ToList();
                table.SetData(AuroraTableData.FromItems(
                    rows,
                    ("指令", "230", row => row.Name),
                    ("类", "76", row => "log"),
                    ("只读", "48", row => "是"),
                    ("说明", AuroraTableColumn.Star, row => row.Text)));
                table.SetRowActions([
                    new AuroraRowAction("a", "详情"),
                    new AuroraRowAction("b", "填入"),
                ]);

                // 复算排在 Loaded 优先级上，最多三轮，要让它们全跑完。
                for (var i = 0; i < 5; i++)
                {
                    UiTestHost.Pump();
                    host.UpdateLayout();
                }

                host.UpdateLayout();
                Assert.All(
                    Descendants<ScrollViewer>(table),
                    scroll => Assert.True(
                        scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                        $"内容宽 {scroll.ExtentWidth}，可视区 {scroll.ViewportWidth}，"
                        + $"表宽 {table.ActualWidth}——表格长出了横向滚动条"));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void TrimmedCellsAreDetectedSoTheirToolTipCanBeTurnedOn()
    {
        UiTestHost.RunSta(() =>
        {
            // 必须给显式 Width：没有它时 WPF 会把"未被裁剪的期望尺寸"当作 RenderSize，
            // 一个脱离容器的 TextBlock 的 ActualWidth 会是整段文字的宽度。
            // 真实单元格由列宽限住，所以这里用显式宽度模拟那个约束。
            var wide = new TextBlock
            {
                Text = new string('说', 60),
                Width = 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var narrow = new TextBlock
            {
                Text = "短",
                Width = 200,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            foreach (var block in new[] { wide, narrow })
            {
                block.Measure(new Size(1000, 100));
                block.Arrange(new Rect(0, 0, block.Width, 100));
            }

            {
                // 放不下的那格才需要提示；放得下的弹出来只会是一模一样的一行字。
                Assert.True(AuroraTable.IsTrimmed(wide));
                Assert.False(AuroraTable.IsTrimmed(narrow));
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
