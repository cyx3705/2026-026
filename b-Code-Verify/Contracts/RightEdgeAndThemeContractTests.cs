using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Widgets;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 1.26.0：表格右缘锁死（REQ-UI-126）、控制面板分隔线跟随主题（REQ-UI-126）、
/// 浅色令牌对齐 OneHistory 站点（REQ-UI-127）。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class RightEdgeAndThemeContractTests
{
    private static readonly Uri LightTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.xaml", UriKind.Relative);

    private static readonly Uri DarkTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.Dark.xaml", UriKind.Relative);

    /// <summary>
    /// 最右一列的右侧线就是表格右缘，拖了也会被分摊拨回去——必须收起；其余列照常可拖。
    /// 有行操作列时，从最后一列数据列起全部收起（行操作列宽度固定，那条线同样拖不动）。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RightmostGripperIsHiddenAndTheOthersStayDraggable(bool withActions)
    {
        UiTestHost.RunSta(() =>
        {
            var (table, host) = Build(withActions);
            try
            {
                var grippers = Grippers(table);
                var expectedLocked = withActions ? 2 : 1;
                var columns = grippers.Count;
                Assert.Equal(withActions ? 4 : 3, columns);

                for (var index = 0; index < columns; index++)
                {
                    var locked = index >= columns - expectedLocked;
                    Assert.True(
                        grippers[index].Gripper.Visibility == (locked ? Visibility.Collapsed : Visibility.Visible),
                        $"第 {index} 列（{grippers[index].Header.Content}）拖拽线应{(locked ? "收起" : "可见")}");
                }
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>
    /// 拖宽中间一列：右缘不动，差额由最后一列吸收；之后的分摊轮次不把它弹回声明比例。
    /// </summary>
    [Fact]
    public void DraggingAColumnKeepsTheRightEdgeLocked()
    {
        UiTestHost.RunSta(() =>
        {
            var (table, host) = Build(withActions: false);
            try
            {
                var grippers = Grippers(table);
                var first = grippers[0].Header.Column!;
                var last = grippers[^1].Header.Column!;
                var before = grippers.Sum(entry => entry.Header.Column!.ActualWidth);
                var firstBefore = first.ActualWidth;
                var lastBefore = last.ActualWidth;

                var thumb = grippers[0].Gripper;
                thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
                thumb.RaiseEvent(new DragDeltaEventArgs(40, 0));
                thumb.RaiseEvent(new DragCompletedEventArgs(40, 0, false));
                Settle(host);

                var after = grippers.Sum(entry => entry.Header.Column!.ActualWidth);
                Assert.InRange(after, before - 1, before + 1);
                Assert.InRange(first.ActualWidth, firstBefore + 30, firstBefore + 50);
                Assert.InRange(last.ActualWidth, lastBefore - 50, lastBefore - 30);

                // 再触发一次外部缩放：按拖出来的比例走，第一列仍比拖之前宽。
                host.Width += 200;
                Settle(host);
                Assert.True(first.ActualWidth > firstBefore + 30, $"拖宽后又被弹回：{first.ActualWidth} vs {firstBefore}");
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>
    /// 深浅切换后控制面板的线必须重画成新颜色。此前在 OnRender 里现取资源，
    /// 版面不变就不重画，线停在旧主题的颜色上。判据取实际渲染出来的像素，不只看属性。
    /// </summary>
    [Fact]
    public void PanelBoardHairlineFollowsTheTheme()
    {
        UiTestHost.RunSta(() =>
        {
            var board = new AuroraPanelBoard();
            board.SetRows(
            [
                new BoardRow
                {
                    Cells =
                    [
                        new BoardCell(new TextBlock { Text = "左" }, 80, false),
                        new BoardCell(new TextBlock { Text = "右" }, 80, true),
                    ],
                },
            ]);

            var tokens = new ResourceDictionary { Source = LightTokensUri };
            var host = new Window
            {
                Content = board,
                Width = 400,
                Height = 120,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            host.Resources.MergedDictionaries.Add(tokens);

            try
            {
                host.Show();
                UiTestHost.Pump();
                Assert.Equal(Token(LightTokensUri, "Aurora.Brush.Hairline"), GutterColor(board));

                host.Resources.MergedDictionaries.Remove(tokens);
                host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DarkTokensUri });
                UiTestHost.Pump();
                board.UpdateLayout();
                UiTestHost.Pump();

                Assert.Equal(Token(DarkTokensUri, "Aurora.Brush.Hairline"), GutterColor(board));
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>
    /// 浅色按深色的原则重配(1.27.0,REQ-UI-129):正文与链接色对比度过 AA,文字与底色同一暖色相,
    /// 三层底色拉开。按比值判而不是按色值判——以后微调色值不必改测试,退回到发浑的旧值才会红。
    /// </summary>
    [Fact]
    public void LightPaletteReadsAsWellAsDark()
    {
        UiTestHost.RunSta(() =>
        {
            var surface = Token(LightTokensUri, "Aurora.Brush.Surface");
            var alt = Token(LightTokensUri, "Aurora.Brush.SurfaceAlt");
            var canvas = Token(LightTokensUri, "Aurora.Brush.Canvas");
            var accent = Token(LightTokensUri, "Aurora.Brush.Accent");
            var secondary = Token(LightTokensUri, "Aurora.Brush.TextSecondary");

            // 表格可点列与链接用 Accent 当字色:卡片与条带上都要过 AA 4.5
            Assert.True(Contrast(accent, surface) >= 4.5, $"Accent/Surface {Contrast(accent, surface):F2}");
            Assert.True(Contrast(accent, alt) >= 4.5, $"Accent/SurfaceAlt {Contrast(accent, alt):F2}");
            Assert.True(Contrast(Token(LightTokensUri, "Aurora.Brush.TextOnAccent"), accent) >= 4.5, "主按钮白字");
            Assert.True(Contrast(secondary, alt) >= 4.5, $"TextSecondary/SurfaceAlt {Contrast(secondary, alt):F2}");
            // 卡片要从底色里浮起来(旧值 1.09 分不开)
            Assert.True(Contrast(surface, canvas) >= 1.15, $"Surface/Canvas {Contrast(surface, canvas):F2}");
            // 文字不能是冷色:红通道不低于蓝通道(旧值 #1F2328 / #6B7280 都偏蓝)
            foreach (var key in new[] { "Aurora.Brush.TextPrimary", "Aurora.Brush.TextSecondary", "Aurora.Brush.TextDisabled" })
            {
                var c = Token(LightTokensUri, key);
                Assert.True(c.R >= c.B, $"{key} 偏冷:{c}");
            }
        });
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) =>
            0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static (AuroraTable Table, Window Host) Build(bool withActions)
    {
        var table = new AuroraTable();
        var host = new Window
        {
            Content = table,
            Width = 900,
            Height = 320,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        host.Show();
        table.SetData(AuroraTableData.FromItems(
            Enumerable.Range(0, 5).Select(index => "row" + index),
            ("名称", "150", row => row),
            ("类别", "70", _ => "模块"),
            ("说明", AuroraTableColumn.Star, _ => "说明文字")));
        if (withActions)
            table.SetRowActions([new AuroraRowAction("open", "打开")]);
        Settle(host);
        return (table, host);
    }

    private static void Settle(Window host)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            host.UpdateLayout();
            UiTestHost.Pump();
        }
    }

    /// <summary>按可视顺序（从左到右）列出真正列的表头与其拖拽线。</summary>
    private static List<(GridViewColumnHeader Header, Thumb Gripper)> Grippers(AuroraTable table)
        => Descendants<GridViewColumnHeader>(table)
            .Where(header => header.Column != null && header.Role == GridViewColumnHeaderRole.Normal)
            .OrderBy(header => header.TranslatePoint(new Point(0, 0), table).X)
            .Select(header => (header, Assert.IsAssignableFrom<Thumb>(header.Template.FindName("PART_HeaderGripper", header))))
            .ToList();

    private static Color GutterColor(AuroraPanelBoard board)
    {
        board.UpdateLayout();
        var offset = VisualTreeHelper.GetOffset(board);
        var width = (int)Math.Ceiling(board.ActualWidth + offset.X);
        var height = (int)Math.Ceiling(board.ActualHeight + offset.Y);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(board);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var line = board.CellBounds[0][0];
        var gutter = (int)Math.Round(((line[0].Right + line[1].Left) / 2) + offset.X);
        for (var x = Math.Max(0, gutter - 1); x <= Math.Min(width - 1, gutter + 1); x++)
        {
            for (var y = 0; y < height; y++)
            {
                var at = (y * stride) + (x * 4);
                if (pixels[at + 3] == 255)
                    return Color.FromRgb(pixels[at + 2], pixels[at + 1], pixels[at]);
            }
        }

        throw new Xunit.Sdk.XunitException("空档里没有画出分隔线");
    }

    private static Color Token(Uri source, string key)
    {
        // 测试进程没有 Application：pack 协议要先被注册，否则相对 URI 报「前缀无法识别」。
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new TextBlock();
        return Assert.IsType<SolidColorBrush>(new ResourceDictionary { Source = source }[key]).Color;
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value)!;

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
