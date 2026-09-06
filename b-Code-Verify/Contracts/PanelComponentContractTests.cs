using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HistoryAurora.Shell.Components.Widgets;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Panels;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 面板组件的契约（REQ-UI-008 / REQ-UI-060，协议 V3）。
///
/// V2 定下的两条不变：小组件只有三种，其余一律拒绝；按钮只认动作 id。
/// V3 加的是**版面**这一层，本文件因此多了四类断言：
/// <list type="number">
///   <item>行是声明出来的一等结构，不是 <c>inline</c> 的副产品；</item>
///   <item>元素按最窄宽度排，**放得下就不换行**；</item>
///   <item>余量按行的模式分：均布等比放大，可变全给一个；</item>
///   <item>折行不产生新的声明行——折出来的两截之间没有那条横分隔线。</item>
/// </list>
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PanelComponentContractTests
{
    [Fact]
    public void SourcePicker_DoesNotCommitEmptyOrDuplicateValues()
    {
        UiTestHost.RunSta(() =>
        {
            var commits = new List<string>();
            var picker = new AuroraSourcePicker
            {
                CommitAsync = value =>
                {
                    commits.Add(value);
                    return Task.CompletedTask;
                },
            };

            picker.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Empty(commits);

            picker.Text = @"C:\parts\valve.par";
            picker.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal([@"C:\parts\valve.par"], commits);

            picker.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Single(commits);
        });
    }

    [Fact]
    public void SourcePicker_CommitRefreshesTheOwningPage()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryLog();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.ui.source",
                Domain = "demo",
                CommandClass = "ui",
                Summary = "设置来源",
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
            });
            var bus = new CommandBus(registry, log);
            var actions = new ActionRegistry(bus, log);
            actions.DeclareLocal("HistoryDemo",
            [
                new ActionDeclaration
                {
                    Id = "demo.source.set",
                    Title = "设置来源",
                    Command = "demo.ui.source",
                    Args = new Dictionary<string, string> { ["path"] = "{value}" },
                },
            ]);

            var refreshes = 0;
            var refresher = new PageDataRefresher();
            refresher.Register("HistoryDemo", "mapping", "parts", [], () =>
            {
                refreshes++;
                return Task.CompletedTask;
            });

            var view = new PanelView(
                Parse("""
                    {
                      "id": "mapping-controls", "title": "来源",
                      "rows": [{ "widgets": [{
                        "kind": "sourcePicker", "id": "source",
                        "selectCommand": "aurora.ui.selectfile",
                        "commitAction": "demo.source.set"
                      }] }]
                    }
                    """),
                bus, log, actions, refresher: refresher, pageId: "mapping");

            var picker = Assert.IsType<AuroraSourcePicker>(
                Assert.Single(Elements(view), element => element is AuroraSourcePicker));
            picker.Text = @"C:\parts\valve.par";
            picker.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            UiTestHost.PumpUntil(() => refreshes > 0);

            Assert.Equal(1, refreshes);
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 面板声明的拒绝口径(REQ-UI-059)。每一条对应一种「界面上会变成什么症状」:
    /// 报错文本必须点名出错的那一处,否则下一个作者只会照着猜。
    /// </summary>
    [Theory]
    // 按钮没写 action:点了没反应。报错要说清"为什么不能写指令名"。
    [InlineData("""[ { "widgets": [ { "kind": "button", "text": "执行" } ] } ]""", "action")]
    // 不认识的控件种类:整行渲染不出来。
    [InlineData("""[ { "widgets": [ { "kind": "slider", "id": "speed" } ] } ]""", "slider")]
    // 图标名不在白名单里。
    [InlineData("""[ { "widgets": [ { "kind": "button", "action": "demo.publish", "icon": "rotate" } ] } ]""", "refresh-cw")]
    // 图标只属于按钮,挂到文本上就是个不可点的装饰。
    [InlineData("""[ { "widgets": [ { "kind": "text", "text": "刷新", "icon": "refresh-cw" } ] } ]""", "只属于按钮")]
    // select 没有任何候选来源:下拉永远是空的。
    [InlineData("""[ { "widgets": [ { "kind": "textbox", "id": "pick", "mode": "select" } ] } ]""", "options")]
    // 静态候选与动态候选只能二选一:两个都写,看到哪一份取决于取数回来的时机,
    // 两次打开可能不一样——那种缺陷只会以"偶尔选项不对"的形态出现,查不出来。
    [InlineData("""[ { "widgets": [ { "kind": "textbox", "id": "pick", "mode": "select", "options": [ "甲" ], "optionsSource": { "command": "demo.ui.data" } } ] } ]""", "二选一")]
    // 不认识的行模式。
    [InlineData("""[ { "mode": "justify", "widgets": [ { "kind": "text", "text": "一" } ] } ]""", "justify")]
    // 最窄宽度写成 0 或负数的症状是「那一格没了」,必须在收下声明时判死。
    [InlineData("""[ { "widgets": [ { "kind": "textbox", "id": "note", "minWidth": 0 } ] } ]""", "minWidth")]
    // 控件重名:占位符按 id 取值,按钮拿到的参数取决于构建顺序。
    // **跨行也要判**——行只是版面,取值作用域是整个面板。
    [InlineData("""[ { "widgets": [ { "kind": "textbox", "id": "name" } ] }, { "widgets": [ { "kind": "textbox", "id": "name" } ] } ]""", "name")]
    public void Validate_RejectsDeclarationsThatWouldRenderWrong(string rows, string expectedInError)
    {
        var parsed = PanelDefinitionValidator.Validate(Parse($$"""
            {
              "id": "demo", "title": "演示",
              "rows": {{rows}}
            }
            """));

        Assert.False(parsed.Ok);
        Assert.Contains(expectedInError, parsed.Error);
    }

    [Fact]
    public void Button_RendersWarningBoxWhenActionUndeclared()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: false);
            var view = new PanelView(Valid(), bus, log, actions);

            var widgets = Elements(view);
            // 按钮不渲染成按钮：看起来能点、其实不能，比点了没反应还难查。
            Assert.DoesNotContain(widgets, element => element is Button);
            Assert.Contains(widgets, element => element is Border);
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level == ShellLogLevel.Error && entry.Message.Contains("demo.publish"));
        });
    }

    [Fact]
    public void Button_FiresDeclaredActionWithControlValues()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var executed = new List<string>();
            bus.Executed += (text, _, _) => executed.Add(text);

            var view = new PanelView(Valid(), bus, log, actions);
            Assert.True(view.TrySetValue("note", "第一版"));

            var button = Assert.IsType<Button>(
                Assert.Single(Elements(view), element => element is Button));
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiTestHost.PumpUntil(() => executed.Count > 0);

            // 面板里写的是动作 id；总线上落下的是声明里的指令名。
            Assert.Equal("demo.release.publish note=第一版 channel=stable", Assert.Single(executed));
        });
    }

    [Fact]
    public void Button_RendersControlledRefreshIconWithoutChangingLegacyButtons()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var iconView = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [{ "widgets": [
                    { "kind": "button", "action": "demo.publish", "text": "刷新", "icon": "refresh-cw" }
                  ] }]
                }
                """), bus, log, actions);
            var iconButton = Assert.IsType<Button>(Assert.Single(Elements(iconView)));
            var content = Assert.IsType<StackPanel>(iconButton.Content);
            Assert.IsType<Path>(content.Children[0]);
            Assert.Equal("刷新", Assert.IsType<TextBlock>(content.Children[1]).Text);

            var legacy = new PanelView(Parse("""
                {
                  "id": "legacy", "title": "演示",
                  "rows": [{ "widgets": [
                    { "kind": "button", "action": "demo.publish", "text": "发布" }
                  ] }]
                }
                """), bus, log, actions);
            Assert.Equal("发布", Assert.IsType<Button>(Assert.Single(Elements(legacy))).Content);
        });
    }

    /// <summary>
    /// <c>required</c> 退役（REQ-UI-060）：一个空着的文本框**不再锁住整块面板的按钮**。
    ///
    /// 旧行为是全局的——任何一个必填框为空，面板上每个按钮都拒绝执行。
    /// Janus 因此不敢用它，Mercury 三个数字框全写了 required 却共用同一批按钮。
    /// 参数缺失交给指令自己的 Required 去报，报出来的还是那条指令的话。
    /// </summary>
    [Fact]
    public void Button_IsNotBlockedByAnEmptySiblingTextBox()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var executed = new List<string>();
            bus.Executed += (text, _, _) => executed.Add(text);

            // note 一个字都没填，而旧协议里它是 required。
            var view = new PanelView(Valid(), bus, log, actions);
            var button = Assert.IsType<Button>(
                Assert.Single(Elements(view), element => element is Button));
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiTestHost.PumpUntil(() => executed.Count > 0);

            Assert.Equal("demo.release.publish note=\"\" channel=stable", Assert.Single(executed));
        });
    }

    /// <summary>
    /// 分隔线**不是子元素**。做成子元素就得在排版之前假设一个位置，
    /// 于是折行处会冒出一条贴着行首的竖线——1.9.2 之前竖排面板正是靠子元素画横线的。
    /// </summary>
    [Fact]
    public void DividersAreNotChildrenOfTheBoard()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();
            var view = new PanelView(Valid(), bus, log, actions);
            var board = Board(view);

            // 四个小组件，其中两个文本框各自多一个独立的标签元素 → 六格。
            Assert.Equal(6, board.Children.Count);
        });
    }

    /// <summary>
    /// 标签是**独立的一个元素**（REQ-UI-060）：它自己占一格，因此与输入区之间有一条竖线。
    /// 面板里的控件不带边框，那条线是「这是标签、那是输入区」唯一的分界。
    /// </summary>
    [Fact]
    public void LabelOccupiesItsOwnCellNextToTheControl()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "widgets": [
                    { "kind": "textbox", "id": "note", "label": "说明" }
                  ] } ]
                }
                """), bus, log, actions);

            var board = Board(view);
            board.Measure(new Size(600, double.PositiveInfinity));
            board.Arrange(new Rect(0, 0, 600, board.DesiredSize.Height));

            var line = Assert.Single(Assert.Single(board.CellBounds));
            Assert.Equal(2, line.Count);
            Assert.True(line[0].Right < line[1].Left, "标签与输入区之间没有留出画分隔线的空档");
        });
    }

    [Fact]
    public void SwitchLabelIsRenderedInsideOneEvenlyDistributedControlCell()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [{ "mode": "even", "widgets": [
                    { "kind": "switch", "id": "recognize", "label": "识别特征与草图", "value": "false", "action": "demo.publish", "minWidth": 100 },
                    { "kind": "switch", "id": "continue", "label": "失败继续", "value": "true", "action": "demo.publish", "minWidth": 100 },
                    { "kind": "switch", "id": "mates", "label": "重建装配关系", "value": "false", "action": "demo.publish", "minWidth": 100 }
                  ] }]
                }
                """), bus, log, actions);

            var board = Board(view);
            Assert.Equal(3, board.Children.Count);
            var toggles = board.Children.OfType<ToggleButton>().ToList();
            Assert.Equal(["识别特征与草图", "失败继续", "重建装配关系"],
                toggles.Select(toggle => Assert.IsType<string>(toggle.Content)).ToArray());

            board.Measure(new Size(624, double.PositiveInfinity));
            board.Arrange(new Rect(0, 0, 624, board.DesiredSize.Height));
            var line = Assert.Single(Assert.Single(board.CellBounds));
            Assert.Equal(3, line.Count);
            Assert.Equal(line[0].Width, line[1].Width, 0);
            Assert.Equal(line[1].Width, line[2].Width, 0);
        });
    }

    /// <summary>
    /// 均布：余量按最窄宽度**等比放大**——宽的还是宽、窄的还是窄，一起长。
    /// 不是等宽平分：把一个按钮和一段长说明拉成同一个宽度，两边都不合适。
    /// </summary>
    [Fact]
    public void EvenRowScalesEveryCellProportionally()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "mode": "even", "widgets": [
                    { "kind": "textbox", "id": "a", "label": "", "minWidth": 100 },
                    { "kind": "textbox", "id": "b", "label": "", "minWidth": 200 }
                  ] } ]
                }
                """), bus, log, actions);

            var board = Board(view);
            board.Measure(new Size(624, double.PositiveInfinity));
            board.Arrange(new Rect(0, 0, 624, board.DesiredSize.Height));

            // 624 － 排版面左右各 6 的内缩 = 612 可用宽；扣掉 12 的间距还剩 600。
            // 100:200 等比放大到 200:400。
            var line = Assert.Single(Assert.Single(board.CellBounds));
            Assert.Equal(200, line[0].Width, 0);
            Assert.Equal(400, line[1].Width, 0);
        });
    }

    /// <summary>可变宽度：其余各自停在最窄宽度，余量全给声明了 flex 的那一个。</summary>
    [Fact]
    public void FlexRowGivesTheRemainderToTheDeclaredElement()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "widgets": [
                    { "kind": "textbox", "id": "a", "label": "", "minWidth": 100, "flex": true },
                    { "kind": "textbox", "id": "b", "label": "", "minWidth": 200 }
                  ] } ]
                }
                """), bus, log, actions);

            var board = Board(view);
            board.Measure(new Size(624, double.PositiveInfinity));
            board.Arrange(new Rect(0, 0, 624, board.DesiredSize.Height));

            var line = Assert.Single(Assert.Single(board.CellBounds));
            Assert.Equal(400, line[0].Width, 0);
            Assert.Equal(200, line[1].Width, 0);
        });
    }

    /// <summary>一个都没声明 flex 时，可变的是**最右边**那一个——这是既有面板的样子。</summary>
    [Fact]
    public void FlexDefaultsToTheRightmostElement()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "widgets": [
                    { "kind": "textbox", "id": "a", "label": "", "minWidth": 100 },
                    { "kind": "textbox", "id": "b", "label": "", "minWidth": 200 }
                  ] } ]
                }
                """), bus, log, actions);

            var board = Board(view);
            board.Measure(new Size(624, double.PositiveInfinity));
            board.Arrange(new Rect(0, 0, 624, board.DesiredSize.Height));

            var line = Assert.Single(Assert.Single(board.CellBounds));
            Assert.Equal(100, line[0].Width, 0);
            Assert.Equal(500, line[1].Width, 0);
        });
    }

    /// <summary>
    /// **放得下就不换行，放不下才折**——而折出来的仍然是同一个声明行。
    ///
    /// 这一条是本轮的核心：折行是宽度不够时的应对，不是版面结构的变化。
    /// 让它变出一条横分隔线，等于让窗口宽度去改声明。
    /// </summary>
    [Fact]
    public void RowWrapsOnlyWhenItCannotFitAndStaysOneDeclaredRow()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "widgets": [
                    { "kind": "textbox", "id": "a", "label": "", "minWidth": 100 },
                    { "kind": "textbox", "id": "b", "label": "", "minWidth": 100 },
                    { "kind": "textbox", "id": "c", "label": "", "minWidth": 100 }
                  ] } ]
                }
                """), bus, log, actions);

            var board = Board(view);

            // 336 － 12 内缩 = 324 = 3×100 + 2×12，正好放得下：一个视觉行。
            board.Measure(new Size(336, double.PositiveInfinity));
            Assert.Equal([1], board.LineCounts);

            // 窄一点就放不下了：折成两个视觉行，但**声明行仍然只有一行**。
            board.Measure(new Size(252, double.PositiveInfinity));
            Assert.Equal([2], board.LineCounts);
            Assert.Single(board.CellBounds);
        });
    }

    /// <summary>折出来的视觉行按**母行**的模式分配余量，不退回缺省。</summary>
    [Fact]
    public void WrappedLinesKeepTheParentRowMode()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "mode": "even", "widgets": [
                    { "kind": "textbox", "id": "a", "label": "", "minWidth": 100 },
                    { "kind": "textbox", "id": "b", "label": "", "minWidth": 200 },
                    { "kind": "textbox", "id": "c", "label": "", "minWidth": 100 }
                  ] } ]
                }
                """), bus, log, actions);

            var board = Board(view);
            board.Measure(new Size(324, double.PositiveInfinity));
            board.Arrange(new Rect(0, 0, 324, board.DesiredSize.Height));

            var row = Assert.Single(board.CellBounds);
            Assert.Equal(2, row.Count);

            // 可用宽 312，第一行两个元素正好占满（100 + 12 + 200），没有余量可分。
            Assert.Equal(100, row[0][0].Width, 0);
            Assert.Equal(200, row[0][1].Width, 0);

            // 第二行只剩一个，等比放大在只有一个元素时就是铺满可用宽。
            Assert.Equal(312, row[1][0].Width, 0);
        });
    }

    /// <summary>
    /// 面板底板与控制台过滤器工具条**必须是同一份样式**（REQ-UI-061）。
    ///
    /// 控制台原先自带一份 `Aurora.Segment.Bar`：同一种东西两套边距，改了一处另一处不动，
    /// 实测表现为面板那份撑出一圈明显比控制台粗的边。
    /// </summary>
    [Fact]
    public void PanelSurfaceAndConsoleToolbarShareOneStyle()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var panel = new PanelView(Valid(), bus, log, actions);
            var surface = Assert.IsType<Border>(panel.Content);

            // 两边各自的资源域里都要解析到同一个键。
            // 判「同一个 Style 实例」是不行的：两处各自合并了一份字典，
            // 那样测的是 WPF 的字典缓存，不是这次的改动。
            Assert.Same(panel.TryFindResource("Aurora.Panel.Surface"), surface.Style);

            var console = new HistoryAurora.Shell.HostedPages.Console.ConsoleView(
                new HistoryAurora.Shell.Neutral.Logging.MemoryShellLog(),
                bus,
                new CommandHistory(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"HistoryAurora-panelstyle-{Guid.NewGuid():N}.txt")),
                new HistoryAurora.Shell.Neutral.CommandSurface.DeferredCommandCatalogSession());
            var host = new Window { Content = console, Width = 760, Height = 420, ShowInTaskbar = false };
            host.Show();
            UiTestHost.Pump();

            var shared = console.TryFindResource("Aurora.Panel.Surface");
            var toolbar = FindDescendants<Border>(console)
                .FirstOrDefault(border => border.Style != null && ReferenceEquals(border.Style, shared));

            // 控制台原先自带的那份必须已经不存在，否则它迟早又长回去。
            var strayCopy = console.TryFindResource("Aurora.Segment.Bar");
            host.Close();

            Assert.True(
                toolbar != null,
                "控制台过滤器工具条没有用面板底板那份样式——同一种东西又变成了两套边距");
            Assert.Null(strayCopy);
        });
    }

    /// <summary>
    /// 面板上下的留白必须与控制台顶栏一致（REQ-UI-061）。
    ///
    /// 判据是**排版面相对底板的内缩**，不是"我改了一个常量"：
    /// 此前底板 Padding 2 之外还叠了 4 的内缩与控件自带的 3～6 上下边距，
    /// 于是同一份底板在控制台上薄薄一条、在控制面板里厚得像加了道边框。
    /// </summary>
    [Fact]
    public void PanelAddsNoVerticalInsetBeyondTheSharedSurface()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();
            var view = new PanelView(Valid(), bus, log, actions);
            var board = Board(view);

            Assert.Equal(0, board.Margin.Top);
            Assert.Equal(0, board.Margin.Bottom);

            // 控件自己也不许再带上下边距——它们叠起来同样是"厚边框"。
            foreach (var child in board.Children.OfType<FrameworkElement>())
            {
                Assert.Equal(0, child.Margin.Top);
                Assert.Equal(0, child.Margin.Bottom);
            }
        });
    }

    /// <summary>
    /// 分隔线必须**真的画在屏幕上**。
    ///
    /// 判据是渲染出来的像素，不是"我设了 Style / 加了子元素"。
    /// 本轮之前吃过两次亏：一次是画刷没解析到、元素在但一个像素不画；
    /// 一次是分隔线高度被算成 0。两次在"结构"上都挑不出毛病。
    /// </summary>
    [Fact]
    public void BoardActuallyPaintsItsSeparators()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var view = new PanelView(Valid(), bus, log, actions);
            var host = new Window
            {
                Content = view,
                Width = 620,
                Height = 240,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = Brushes.White,
            };
            host.Show();
            UiTestHost.Pump();

            var board = Board(view);
            var before = string.Join("/", board.LineCounts);
            AssertSeparatorsPainted(board, "初次排版");

            // 收窄后必须折行，线也要跟着换位置重画。
            // OnRender 只在**视觉**失效时跑，排版重算不会自动带上它——
            // 不补一次重画，窗口一改宽度线就留在老位置上。
            host.Width = 260;
            UiTestHost.Pump();
            board.UpdateLayout();
            UiTestHost.Pump();

            Assert.NotEqual(before, string.Join("/", board.LineCounts));
            AssertSeparatorsPainted(board, "收窄折行后");

            host.Close();
        });
    }

    /// <summary>数元素之间空档里的像素：那里只可能是分隔线画出来的。</summary>
    private static void AssertSeparatorsPainted(AuroraPanelBoard board, string stage)
    {
        board.UpdateLayout();

        // RenderTargetBitmap.Render 会**保留被渲染元素相对父级的偏移**：排版面被底板的
        // Padding 与自身 Margin 推开了几个像素，不把偏移算进去就会整体错位，
        // 按理论坐标数像素全部落空——这里踩过一次，误判成"线没画"。
        var offset = VisualTreeHelper.GetOffset(board);
        var width = (int)Math.Ceiling(board.ActualWidth + offset.X);
        var height = (int)Math.Ceiling(board.ActualHeight + offset.Y);
        Assert.True(width > 0 && height > 0, $"{stage}：排版面没有尺寸 {width}x{height}");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(board);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // 只看元素与元素之间的空档。控件都被安排在自己那一格里，画不到空档上，
        // 所以空档里的不透明像素只可能是分隔线——"整块有像素"是不够的判据，
        // 子控件的文字也会让它通过。
        var gutter = -1;
        foreach (var row in board.CellBounds)
        {
            foreach (var line in row)
            {
                if (line.Count < 2)
                    continue;
                gutter = (int)Math.Round(((line[0].Right + line[1].Left) / 2) + offset.X);
                break;
            }

            if (gutter >= 0)
                break;
        }

        Assert.True(gutter is >= 0, $"{stage}：没有任何一行有两个以上的元素，测不到空档");
        Assert.True(gutter < width, $"{stage}：空档位置落在画面之外 {gutter} >= {width}");

        var painted = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = Math.Max(0, gutter - 1); x <= Math.Min(width - 1, gutter + 1); x++)
            {
                if (pixels[(y * stride) + (x * 4) + 3] != 0)
                    painted++;
            }
        }

        Assert.True(
            painted > 0,
            $"{stage}：元素之间一条分隔线都没画出来（"
            + $"折行={string.Join("/", board.LineCounts)} 画面={width}x{height} 空档x={gutter}）");
    }

    /// <summary>面板不得出现滚动条：它只是一块面板，内容多了往下长，不往里滚。</summary>
    [Fact]
    public void PanelNeverPutsItsContentInAScrollViewer()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var view = new PanelView(Valid(), bus, log, actions);
            Assert.Empty(FindDescendants<ScrollViewer>(view));
        });
    }

    private static List<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                found.Add(match);
            found.AddRange(FindDescendants<T>(child));
        }

        if (root is ContentControl { Content: DependencyObject content })
        {
            if (content is T direct)
                found.Add(direct);
            found.AddRange(FindDescendants<T>(content));
        }

        if (root is Border { Child: DependencyObject boxed })
        {
            if (boxed is T direct)
                found.Add(direct);
            found.AddRange(FindDescendants<T>(boxed));
        }

        return found;
    }

    /// <summary>
    /// 页面描述里的行必须原样落到面板上。
    ///
    /// 1.8.9 的实测故障是同一类：<c>BuildPanel</c> 装 PanelDefinition 时漏抄了 orientation，
    /// 症状不是报错——面板照样画出来，只是版面变了，看上去像"分隔线没做"。
    /// 现在漏抄的会是 rows，症状是所有元素挤成一行或散成一列。
    /// </summary>
    [Fact]
    public void PageLevelPanelKeepsTheDeclaredRows()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var rendered = HistoryAurora.Shell.Components.Pages.PageRenderer.Render(
                PagePanel(),
                new HistoryAurora.Shell.Components.Pages.PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = "HistoryDemo",
                    Actions = actions,
                });

            var view = Assert.IsType<PanelView>(rendered.Root);
            var board = Board(view);
            board.Measure(new Size(600, double.PositiveInfinity));

            // 两个声明行；第二行的文本框自带一个标签元素。
            Assert.Equal(2, board.CellBounds.Count);
            Assert.Equal([1, 1], board.LineCounts);
            Assert.Single(board.CellBounds[0][0]);
            Assert.Equal(2, board.CellBounds[1][0].Count);
        });
    }

    private static HistoryAurora.Shell.Components.Pages.PageDescription PagePanel()
    {
        var parsed = HistoryAurora.Shell.Components.Pages.PageDescriptionReader.Read("""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [ {
                "id": "demo", "title": "演示",
                "content": {
                  "type": "panel",
                  "id": "demo-panel",
                  "rows": [
                    { "widgets": [ { "kind": "text", "text": "一" } ] },
                    { "widgets": [ { "kind": "textbox", "id": "note", "label": "说明" } ] }
                  ]
                }
              } ]
            }
            """, "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    // ---------------------------------------------------------------- 装配

    private static PanelDefinition Valid() => Parse("""
        {
          "id": "release", "title": "发布",
          "rows": [
            { "widgets": [ { "kind": "text", "text": "填写说明后发布" } ] },
            { "widgets": [
              { "kind": "textbox", "id": "note", "label": "说明", "flex": true },
              { "kind": "textbox", "id": "channel", "label": "通道", "mode": "select",
                "options": [ "stable", "beta" ] }
            ] },
            { "mode": "even", "widgets": [ { "kind": "button", "action": "demo.publish", "text": "发布" } ] }
          ]
        }
        """);

    private static PanelDefinition Parse(string json)
        => JsonSerializer.Deserialize<PanelDefinition>(json, JsonOptions)!;

    private static (CommandBus Bus, MemoryLog Log, ActionRegistry Actions) Host(bool declare)
    {
        var registry = new CommandRegistry();
        var log = new MemoryLog();

        registry.Register(new CommandDescriptor
        {
            Name = "demo.release.publish",
            Domain = "demo",
            CommandClass = "core",
            Summary = "测试发布",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });

        if (declare)
        {
            registry.Register(new CommandDescriptor
            {
                Name = "demo" + ActionRegistry.ActionsSuffix,
                Domain = "demo",
                CommandClass = "ui",
                Summary = "动作声明",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                    {
                      "schemaVersion": 1,
                      "owner": "HistoryDemo",
                      "actions": [
                        {
                          "id": "demo.publish",
                          "title": "发布",
                          "command": "demo.release.publish",
                          "args": { "note": "{note}", "channel": "{channel}" }
                        }
                      ]
                    }
                    """)),
            });
        }

        var bus = new CommandBus(registry, log);
        return (bus, log, new ActionRegistry(bus, log));
    }

    /// <summary>面板的排版面。</summary>
    private static AuroraPanelBoard Board(PanelView view)
        => Assert.IsType<AuroraPanelBoard>(Assert.IsType<Border>(view.Content).Child);

    /// <summary>排版面上的全部组件元素（文本框标签仍是独立元素，开关描述已并入控件）。</summary>
    private static List<FrameworkElement> Elements(PanelView view)
        => Board(view).Children.OfType<FrameworkElement>().ToList();

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
    }
}
