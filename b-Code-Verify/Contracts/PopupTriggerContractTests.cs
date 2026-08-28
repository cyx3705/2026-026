using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 弹出层的右键触发（REQ-UI-056）。
///
/// **这个能力为什么存在。** Mercury 的扩展坞管理页顶上常驻着一个「加入扩展坞」面板
/// （一个输入框加四个按钮）。它一周也用不到一次，却长期占着页面纵向空间，而这一页真正
/// 要看的是下面那张条目表——REQ-UI-050 之后页面不滚，被它挤掉的高度就是**看不见的行**。
///
/// 弹出层（REQ-UI-012）本来就是为这个问题造的，但它自带一个按钮，仍然占一行。
/// 而右键这条路当时只有表格的行操作菜单有，且那是一个**动作菜单**，放不进输入框。
///
/// 因此本条**不新建组件**：浮层本体、圆角、阴影、收起时机全部沿用 <see cref="AuroraFlyout"/>，
/// 只是多一条打开它的路。写第二套的话，两份浮层的外观会各自演进，
/// 而那种差异浅色下几乎看不出来。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PopupTriggerContractTests
{
    [Fact]
    public void TheCapabilityCountsAsDelivered()
    {
        // 台账按名字销账。trigger 是挂在 popup 上的**能力**，不是一个新节点类型——
        // 混进组件清单会让 type:"popup.trigger" 一边判为已支持、一边渲染成缺件占位。
        Assert.Contains("popup.trigger", PageRenderer.SupportedCapabilities);
        Assert.DoesNotContain("popup.trigger", PageRenderer.SupportedComponents);
    }

    [Fact]
    public void AContextTriggeredPopupTakesNoLayoutSpaceAndOpensOnRightClick()
    {
        UiTestHost.RunSta(() =>
        {
            var rendered = Render(ContextPopupJson);
            var surface = Assert.IsType<Grid>(rendered.Root);

            // 声明的那一格只剩一个 Collapsed 空位：顺序容器会给每个子节点补一段间距，
            // 零尺寸的元素照样留下那段间距，而 Collapsed 的元素连边距都不参与。
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(surface.Children[0]).Visibility);

            // 浮层本体改挂在整页这一层，且**必须可见**——WPF 的 Popup 在 Collapsed 的
            // 父级下开不出来，不抛异常也不触发 Closed，IsOpen 写进去就是 false。
            var flyout = Assert.IsType<AuroraFlyout>(surface.Children[1]);
            Assert.Equal(Visibility.Visible, flyout.Visibility);

            // 右键落点必须命中得到：Panel 的 Background 为 null 时空白处不参与命中测试，
            // 而空白处正是这条路唯一会被右键到的地方。
            Assert.NotNull(surface.Background);

            var window = Show(surface);
            try
            {
                Assert.False(flyout.IsOpen);

                surface.RaiseEvent(RightButtonUp());
                UiTestHost.Pump();

                Assert.True(flyout.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 右键已经被别人用掉时不再弹浮层。
    ///
    /// 表格的行操作菜单就是「别人」：它在冒泡途中把事件标成已处理。反过来，表格在空白处
    /// 主动放弃菜单（<c>AuroraTable.OnRowContextMenuOpening</c> 判无行即取消），
    /// 事件继续往上冒，浮层才弹。「右键行 → 行菜单、右键空白 → 浮层」因此是自然结果，
    /// 不需要谁去协调谁——前提是这里订阅的是冒泡事件且不收已处理的那些。
    /// </summary>
    [Fact]
    public void AHandledRightClickDoesNotAlsoOpenTheFlyout()
    {
        UiTestHost.RunSta(() =>
        {
            var rendered = Render(ContextPopupJson);
            var surface = Assert.IsType<Grid>(rendered.Root);
            var flyout = Assert.IsType<AuroraFlyout>(surface.Children[1]);

            var window = Show(surface);
            try
            {
                var args = RightButtonUp();
                args.Handled = true;
                surface.RaiseEvent(args);
                UiTestHost.Pump();

                Assert.False(flyout.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 缺省仍然是按钮式，且整页不被包一层。
    ///
    /// 这条钉住的是「不改既有页面」：1.7.0 起所有 popup 都是按钮式的，
    /// 加一个字段不该让它们的版面当场变样。
    /// </summary>
    [Fact]
    public void WithoutATriggerThePopupKeepsItsOwnButton()
    {
        UiTestHost.RunSta(() =>
        {
            var rendered = Render(PlainPopupJson);

            var flyout = Assert.IsType<AuroraFlyout>(rendered.Root);
            Assert.Equal(Visibility.Visible, flyout.Visibility);
        });
    }

    /// <summary>
    /// 同一页只接一个。右键只有一次，接第二个的话「弹出哪一个」取决于建页顺序，
    /// 而那个顺序不受任何东西保证——与选择通道的抢注规则同一条理由。
    /// 被拒的那个渲染成写明原因的牌子，不是静默少一个浮层。
    /// </summary>
    [Fact]
    public void OnlyTheFirstContextPopupOnAPageIsWiredUp()
    {
        UiTestHost.RunSta(() =>
        {
            var rendered = Render(
                "{ \"type\": \"stack\", \"children\": [ " + ContextPopupJson + ", " + ContextPopupJson + " ] }");

            var stack = Assert.IsType<Grid>(Assert.IsType<Grid>(rendered.Root).Children[0]);

            // 第一个被接上：它在页面里只留一个空位，本体挂到了整页那一层。
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(stack.Children[0]).Visibility);

            var refused = Assert.IsType<Border>(stack.Children[1]);
            var caption = Assert.IsType<TextBlock>(refused.Child);
            Assert.Contains("右键只有一次", caption.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// trigger 写错要说出来。静默按缺省处理的症状是「右键怎么点都没反应」，
    /// 而版面上确实多了个按钮，看上去就像这个组件本来就长这样。
    /// </summary>
    [Fact]
    public void AnUnknownTriggerFallsBackToTheButtonAndSaysSo()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();
            var rendered = PageRenderer.Render(
                Page(TypoTriggerJson),
                new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });

            var flyout = Assert.IsType<AuroraFlyout>(rendered.Root);
            Assert.Equal(Visibility.Visible, flyout.Visibility);
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level >= ShellLogLevel.Warn
                && entry.Message.Contains("trigger=rightclick", StringComparison.Ordinal));

            // 写错不是缺件：组件是有的，缺的是声明。记进缺件表会让模块去申请一个已有的东西。
            Assert.Empty(rendered.MissingComponents);
        });
    }

    private const string ContextPopupJson =
        "{ \"type\": \"popup\", \"id\": \"dock-add\", \"text\": \"加入\", \"trigger\": \"context\","
        + " \"widgets\": ["
        + " { \"kind\": \"textbox\", \"id\": \"value\", \"label\": \"名称\" },"
        + " { \"kind\": \"button\", \"action\": \"demo.pin\", \"text\": \"固定\", \"inline\": true } ] }";

    private const string PlainPopupJson =
        "{ \"type\": \"popup\", \"id\": \"plain\", \"text\": \"更多\","
        + " \"widgets\": [ { \"kind\": \"button\", \"action\": \"demo.pin\", \"text\": \"固定\" } ] }";

    private const string TypoTriggerJson =
        "{ \"type\": \"popup\", \"id\": \"typo\", \"trigger\": \"rightclick\","
        + " \"widgets\": [ { \"kind\": \"button\", \"action\": \"demo.pin\", \"text\": \"固定\" } ] }";

    private static MouseButtonEventArgs RightButtonUp()
        => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent,
        };

    private static Window Show(FrameworkElement content)
    {
        var window = new Window
        {
            Content = content,
            Width = 400,
            Height = 300,
            Opacity = 0,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        window.Show();
        UiTestHost.Pump();
        return window;
    }

    private static RenderedPage Render(string contentJson)
    {
        var (bus, log, actions) = Host();
        return PageRenderer.Render(
            Page(contentJson),
            new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });
    }

    private static PageDescription Page(string contentJson)
    {
        var parsed = PageDescriptionReader.Read(
            "{ \"schemaVersion\": 1, \"owner\": \"HistoryDemo\", \"pages\": [ "
            + "{ \"id\": \"demo\", \"title\": \"演示\", \"content\": " + contentJson + " } ] }",
            "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    private static (CommandBus Bus, TestLog Log, ActionRegistry Actions) Host()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "demo.item.pin",
            Domain = "demo",
            CommandClass = "item",
            Summary = "固定条目",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "demo" + ActionRegistry.ActionsSuffix,
            Domain = "demo",
            CommandClass = "ui",
            Summary = "动作声明",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                "{ \"schemaVersion\": 1, \"owner\": \"HistoryDemo\", \"actions\": [ "
                + "{ \"id\": \"demo.pin\", \"title\": \"固定\", \"command\": \"demo.item.pin\","
                + " \"args\": { \"name\": \"{value}\" } } ] }")),
        });

        var log = new TestLog();
        var bus = new CommandBus(registry, log);
        var actions = new ActionRegistry(bus, log);
        actions.ReloadAsync().GetAwaiter().GetResult();
        return (bus, log, actions);
    }

    private sealed class TestLog : IShellLog
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
