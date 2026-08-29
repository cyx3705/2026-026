using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Logging;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.Table;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 控制面板发布通道（REQ-UI-045）与页面切换容器（REQ-UI-046）。
///
/// **这两条一起才构成「三个页签收进一页」**：选项框把当前选中的标题发上通道，
/// 切换容器按它换掉下面那一批组件。缺前一条，选项框的值只能填进动作参数，
/// 界面上什么都不会动；缺后一条，通道有了值也没有任何节点在听。
///
/// 换的是**组件**不是页面：三支各自还是普通的 stack / table / panel，
/// 只是同一时刻只有一支挂在树上。收成三个页签是停靠层的事，收成一个控件是渲染器的事。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PageSwitchContractTests
{
    /// <summary>
    /// 轮换选项框一转，下面就换一支——而且**初值那一支在页面打开时就已经在**。
    ///
    /// 不发初值的话，页面一打开停在"还没选"的空壳态，而用户明明看见选项框里写着一个值；
    /// 那种不一致在界面上与"这块坏了"没有区别。
    /// </summary>
    [Fact]
    public void RotatingTheOptionBoxSwapsTheComponentsBelowIt()
    {
        UiTestHost.RunSta(() =>
        {
            using var host = new Harness();
            var root = host.RenderPage();
            host.Mount(root);

            var box = Descendants<AuroraOptionBox>(root).Single();
            Assert.Equal("规则", box.SelectedItem);

            // 初值那一支：第一支是一段文字。
            Assert.Single(Descendants<TextBlock>(root), text => text.Text == "第一支");
            Assert.DoesNotContain(Descendants<AuroraTable>(root), _ => true);

            box.SelectedItem = "历史";
            UiTestHost.Pump();

            // 换掉的是组件本身，不是显隐：上一支整个离开了可视树。
            Assert.DoesNotContain(Descendants<TextBlock>(root), text => text.Text == "第一支");
            Assert.Single(Descendants<AuroraTable>(root));
        });
    }

    /// <summary>
    /// 没被切到过的分支不取数。
    ///
    /// **这是懒挂树而不是 Collapsed 的唯一理由**：折叠元素照样收 Loaded，
    /// 于是三支的取数会在开页那一刻一起打出去。Janus 的落地状态那一支每次跑两条
    /// <c>git ls-files</c>，没人看的两支不该付这个钱。
    /// </summary>
    [Fact]
    public void ABranchNobodyOpenedNeverFetches()
    {
        UiTestHost.RunSta(() =>
        {
            using var host = new Harness();
            var root = host.RenderPage();
            host.Mount(root);

            Assert.True(UiTestHost.PumpUntil(() => host.Fetched.Count >= 0));
            Assert.Empty(host.Fetched);

            var box = Descendants<AuroraOptionBox>(root).Single();
            box.SelectedItem = "历史";
            Assert.True(
                UiTestHost.PumpUntil(() => host.Fetched.Count == 1),
                "切到取数分支之后仍然没有取数");
        });
    }

    /// <summary>
    /// 切走再切回来用的是**同一个控件实例**，而且不重取。
    ///
    /// 重建的话，表格的滚动位置、筛选词和选中行会在每次切换时消失，
    /// 而那些正是人切走之前留下的上下文；重取则是把一次纯粹的视图切换
    /// 变成一次 Git 调用。
    /// </summary>
    [Fact]
    public void SwitchingBackReusesTheSameComponentWithoutRefetching()
    {
        UiTestHost.RunSta(() =>
        {
            using var host = new Harness();
            var root = host.RenderPage();
            host.Mount(root);

            var box = Descendants<AuroraOptionBox>(root).Single();
            box.SelectedItem = "历史";
            Assert.True(UiTestHost.PumpUntil(() => host.Fetched.Count == 1));

            var first = Descendants<AuroraTable>(root).Single();

            box.SelectedItem = "规则";
            UiTestHost.Pump();
            box.SelectedItem = "历史";
            UiTestHost.Pump();

            Assert.Same(first, Descendants<AuroraTable>(root).Single());
            UiTestHost.PumpFor(120);
            Assert.Single(host.Fetched);
        });
    }

    /// <summary>
    /// source 写坏时给出一块写着原因的牌子，而不是空白，也**不算缺件**——
    /// 组件是有的，缺的是声明。缺件会被自动记进组件申请台账，
    /// 把声明错误也记进去等于让模块不停申请一个已经交付的东西。
    /// </summary>
    [Fact]
    public void ASwitchWithoutAUsableSourceSaysWhyInsteadOfGoingBlank()
    {
        UiTestHost.RunSta(() =>
        {
            using var host = new Harness();
            foreach (var content in new[]
                     {
                         """{ "type": "switch", "children": [ { "type": "text", "case": "甲", "text": "甲" } ] }""",
                         """{ "type": "switch", "source": "{selection.demo.section.value}" }""",
                     })
            {
                var rendered = host.Render(content);
                Assert.Empty(rendered.MissingComponents);
                var box = Assert.IsType<Border>(rendered.Root);
                Assert.Contains(
                    "切换容器",
                    ((TextBlock)box.Child).Text,
                    StringComparison.Ordinal);
            }
        });
    }

    /// <summary>
    /// 重复的 case 与够不着的分支必须出声。两者的症状相同且都查不出来：
    /// 某一支怎么点都出不来，而界面上没有任何地方说过这件事。
    /// </summary>
    [Fact]
    public void DuplicateAndUnreachableBranchesAreReportedOutLoud()
    {
        UiTestHost.RunSta(() =>
        {
            using var host = new Harness();
            host.Render("""
                {
                  "type": "switch",
                  "source": "{selection.demo.section.value}",
                  "children": [
                    { "type": "text", "case": "甲", "text": "一" },
                    { "type": "text", "case": "甲", "text": "二" },
                    { "type": "text", "text": "三" }
                  ]
                }
                """);

            var warnings = host.Log.Snapshot()
                .Where(entry => entry.Level >= ShellLogLevel.Warn)
                .Select(entry => entry.Message)
                .ToList();

            Assert.Contains(warnings, message => message.Contains("case 重复", StringComparison.Ordinal));
            Assert.Contains(warnings, message => message.Contains("永远不会被选中", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// 发布方与表格走同一个台账、同一条抢注规则：同名通道只认第一个声明方。
    /// 后到的照发的话，"另一处的显示莫名其妙跟着我变"，而两处都看不出是谁在改。
    /// </summary>
    [Fact]
    public void TheSecondPublisherClaimingAChannelIsRefusedAndStaysSilent()
    {
        UiTestHost.RunSta(() =>
        {
            using var host = new Harness();
            var first = host.Render(PanelJson("panel-a"));
            var second = host.Render(PanelJson("panel-b"));
            host.Mount(first.Root);
            host.Mount(second.Root);

            Assert.Contains(
                host.Log.Snapshot(),
                entry => entry.Level >= ShellLogLevel.Warn
                         && entry.Message.Contains("已由", StringComparison.Ordinal));

            // 抢注失败的那个框改值时不得再往通道上写。
            Descendants<AuroraOptionBox>(second.Root).Single().SelectedItem = "历史";
            UiTestHost.Pump();
            Assert.Equal("规则", host.Channels.Value("demo.section", "value"));
        });
    }

    // ---------------------------------------------------------------- 装配

    private static string PanelJson(string id) => $$"""
        {
          "type": "panel",
          "id": "{{id}}",
          "text": "项目操作",
          "rows": [ { "widgets": [
            { "kind": "textbox", "id": "section", "label": "子页面", "mode": "select",
              "channel": "demo.section", "options": [ "规则", "历史" ] }
          ] } ]
        }
        """;

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T self)
            yield return self;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var nested in Descendants<T>(VisualTreeHelper.GetChild(root, i)))
                yield return nested;
        }
    }

    /// <summary>一页：上面是带发布通道的控制面板，下面是跟着它换的切换容器。</summary>
    private sealed class Harness : IDisposable
    {
        private const string PageJson = """
            {
              "type": "stack",
              "gap": "normal",
              "children": [
                {
                  "type": "panel",
                  "id": "ops",
                  "text": "项目操作",
                  "rows": [ { "widgets": [
                    { "kind": "textbox", "id": "section", "label": "子页面", "mode": "select",
                      "channel": "demo.section", "options": [ "规则", "历史" ] }
                  ] } ]
                },
                {
                  "type": "switch",
                  "id": "sections",
                  "source": "{selection.demo.section.value}",
                  "children": [
                    { "type": "text", "case": "规则", "text": "第一支" },
                    {
                      "type": "table",
                      "case": "历史",
                      "columns": [ { "key": "sha", "title": "提交" } ],
                      "dataSource": { "command": "demo.history" }
                    }
                  ]
                }
              ]
            }
            """;

        private readonly List<Window> _windows = [];

        public Harness()
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.history",
                Domain = "demo",
                CommandClass = "history",
                Summary = "取历史",
                Readonly = true,
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(_ =>
                {
                    Fetched.Add("demo.history");
                    return CommandResult.Ok("[]");
                }),
            });

            Log = new MemoryShellLog();
            Bus = new CommandBus(registry, Log);
            Actions = new ActionRegistry(Bus, Log);
            Channels = new SelectionChannels();
            Refresher = new PageDataRefresher(Channels);
        }

        public CommandBus Bus { get; }

        public MemoryShellLog Log { get; }

        public ActionRegistry Actions { get; }

        public SelectionChannels Channels { get; }

        public PageDataRefresher Refresher { get; }

        public List<string> Fetched { get; } = [];

        public FrameworkElement RenderPage() => Render(PageJson).Root;

        public RenderedPage Render(string contentJson)
        {
            var parsed = PageDescriptionReader.Read($$"""
                {
                  "schemaVersion": 1,
                  "owner": "HistoryDemo",
                  "pages": [ { "id": "projops", "title": "项目操作", "content": {{contentJson}} } ]
                }
                """, "HistoryDemo");
            Assert.True(parsed.Ok, parsed.Error);

            return PageRenderer.Render(
                parsed.Value!.Pages[0],
                new PageRenderContext
                {
                    Bus = Bus,
                    Log = Log,
                    Owner = "HistoryDemo",
                    Actions = Actions,
                    Channels = Channels,
                    Refresher = Refresher,
                });
        }

        /// <summary>没进可视树的控件，样式与 Loaded 都还没发生——取数因此也不会开始。</summary>
        public Window Mount(FrameworkElement root)
        {
            var window = new Window
            {
                Content = root,
                Width = 420,
                Height = 320,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            _windows.Add(window);
            window.Show();
            UiTestHost.Pump();
            return window;
        }

        public void Dispose()
        {
            foreach (var window in _windows)
                window.Close();
        }
    }
}
