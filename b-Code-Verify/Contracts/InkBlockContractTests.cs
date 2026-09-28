using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Widgets;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 1.28.0：OneHistory 文艺工业风的高对比模块（REQ-UI-132）——墨色条、墨色选中块、粗细两级墨线，
/// 权威在 2026-031 z-OneHistoryID/网站风格.md §2.3–2.4。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class InkBlockContractTests
{
    private static readonly Uri LightTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.xaml", UriKind.Relative);

    private static readonly Uri DarkTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.Dark.xaml", UriKind.Relative);

    /// <summary>
    /// 墨色令牌只是别名，不引入新颜色：Ink = 本主题正文色、OnInk = 本主题底色，
    /// AccentOnInk / TextSecondaryOnInk 取另一套主题的对应值。反白字与墨块上的金色编号都要过 AA。
    /// </summary>
    [Fact]
    public void InkTokensAreAliasesAndReadable()
    {
        UiTestHost.RunSta(() =>
        {
            foreach (var (self, other) in new[] { (LightTokensUri, DarkTokensUri), (DarkTokensUri, LightTokensUri) })
            {
                var ink = Token(self, "Aurora.Brush.Ink");
                Assert.Equal(Token(self, "Aurora.Brush.TextPrimary"), ink);
                Assert.Equal(Token(self, "Aurora.Brush.Canvas"), Token(self, "Aurora.Brush.OnInk"));
                Assert.Equal(Token(other, "Aurora.Brush.Accent"), Token(self, "Aurora.Brush.AccentOnInk"));
                Assert.Equal(Token(other, "Aurora.Brush.TextSecondary"), Token(self, "Aurora.Brush.TextSecondaryOnInk"));

                Assert.True(Contrast(Token(self, "Aurora.Brush.OnInk"), ink) >= 4.5, $"{self} 反白字");
                // 墨块上的金色只用于悬停态与编号，按非正文的 3:1 判。深色是浅色主题色落在米色墨块上，
                // 实测 4.2:1——与站点 accent-on-ink 同值，改它要先改权威 README。
                Assert.True(Contrast(Token(self, "Aurora.Brush.AccentOnInk"), ink) >= 3.0,
                    $"{self} 墨块上的金色 {Contrast(Token(self, "Aurora.Brush.AccentOnInk"), ink):F2}");
            }
        });
    }

    /// <summary>
    /// 表格：表头是反色圆角块（墨底、字反白）；链接列是墨色字（金色只做编号）；
    /// 选中行是墨色块，格里的字与链接一起反白。1.27 之前是灰底圆角表头、金字链接列、淡金选中。
    /// </summary>
    [Fact]
    public void TableUsesInkHeaderAndInkSelection()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new("name", "名称", "*", new("open")), new("note", "说明", "*")],
                [
                    new Dictionary<string, string> { ["name"] = "甲", ["note"] = "一" },
                    new Dictionary<string, string> { ["name"] = "乙", ["note"] = "二" },
                ]));
            _ = Token(LightTokensUri, "Aurora.Brush.Ink"); // 注册 pack 协议
            var window = new Window { Content = table, Width = 500, Height = 240, ShowInTaskbar = false };
            // 测试进程没有 Application：令牌平时由主窗体继承下来，这里给窗口自己并一份
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = LightTokensUri });
            try
            {
                window.Show();
                UiTestHost.Pump();

                var ink = Brush(table, "Aurora.Brush.Ink");
                var onInk = Brush(table, "Aurora.Brush.OnInk");
                var text = Brush(table, "Aurora.Brush.TextPrimary");

                var header = Descendants<Border>(table)
                    .Single(border => border.Child is GridViewHeaderRowPresenter);
                Assert.Equal(ink, ((SolidColorBrush)header.Background).Color);
                Assert.True(header.CornerRadius.TopLeft > 0, "表头是圆角块");
                var column = Descendants<GridViewColumnHeader>(table)
                    .First(item => item.Role == GridViewColumnHeaderRole.Normal);
                Assert.Equal(onInk, ((SolidColorBrush)column.Foreground).Color);

                var rows = Descendants<ListViewItem>(table).ToList();
                Assert.Equal(2, rows.Count);
                var link = Descendants<Button>(rows[0]).Single();
                Assert.Equal(text, ((SolidColorBrush)link.Foreground).Color);

                rows[0].IsSelected = true;
                UiTestHost.Pump();

                Assert.Equal(onInk, ((SolidColorBrush)link.Foreground).Color);
                var cell = Descendants<TextBlock>(rows[0]).First(block => block.Text == "一");
                Assert.Equal(onInk, ((SolidColorBrush)cell.Foreground).Color);
                var block = Descendants<Border>(rows[0]).Single(border => border.Name == "Sel");
                Assert.True(block.IsVisible);
                Assert.Equal(ink, ((SolidColorBrush)block.Background).Color);
                Assert.True(block.CornerRadius.TopLeft > 0, "墨色选中块是圆角块");

                // 未选中的行不受影响
                var other = Descendants<TextBlock>(rows[1]).First(block => block.Text == "二");
                Assert.Equal(text, ((SolidColorBrush)other.Foreground).Color);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>段标题按「标题 · 说明」拆开：说明进墨色条反白；没有分隔符时整句是标题、条里不写字。</summary>
    [Fact]
    public void SectionHeadingSplitsNoteIntoInkBar()
    {
        UiTestHost.RunSta(() =>
        {
            var heading = new AuroraSectionHeading();
            heading.SetText("2026-028-LocalChat · 28 个节点");
            Assert.Equal("2026-028-LocalChat", heading.Title);
            Assert.Equal("28 个节点", heading.Note);

            heading.SetText("常用页面");
            Assert.Equal("常用页面", heading.Title);
            Assert.Equal("", heading.Note);

            heading.SetText(null);
            Assert.Equal("", heading.Title);
        });
    }

    private static Color Brush(FrameworkElement scope, string key) =>
        Assert.IsType<SolidColorBrush>(scope.FindResource(key)).Color;

    private static Color Token(Uri source, string key)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new TextBlock();
        return Assert.IsType<SolidColorBrush>(new ResourceDictionary { Source = source }[key]).Color;
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (lo > hi)
            (hi, lo) = (lo, hi);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
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
