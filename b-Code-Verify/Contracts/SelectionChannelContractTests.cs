using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Panels;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 选择通道（REQ-UI-041）与顺序容器的尺寸分配（REQ-UI-042）。
///
/// **这两条为什么存在**：1.8.14 把 <c>button</c> / <c>input</c> 收出页面节点时，
/// 页面按钮的 <c>invoke</c> 与 <c>enabledWhen</c> 一并退役，「表格选中行 → 按钮变可用」
/// 这条链路整整断了一版；而 Janus 的项目表在中央页、操作面板在左侧页，
/// 就算 <c>enabledWhen</c> 还在，它的落点（页内节点 id）也跨不过页边界。
/// 通道把落点换成界面级的名字，这条链路才回得来。
///
/// 同一轮的实测故障还有一条：项目总览页的表格塞在 <c>stack</c> 里，撑满整页并且滚不动。
/// 成因是 StackPanel 在排列方向上以无穷尺寸量子元素，表格因此把每一行都画出来——
/// **不报错**，只是这一页用不了。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class SelectionChannelContractTests
{
    /// <summary>
    /// 同一行再发一次不能再通知。选项框发布之后可能被跟随回写，
    /// switch 与取数都挂在 Changed 上——值没变还通知，就是自激回路。
    /// </summary>
    [Fact]
    public void PublishingTheSameRowAgainDoesNotRaiseChanged()
    {
        var channels = new SelectionChannels();
        Assert.True(channels.TryDeclare("demo", "HistoryDemo", "origin", out _));
        var fires = 0;
        channels.Changed += (_, _) => fires++;

        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "alpha" };
        channels.Publish("demo", row);
        channels.Publish("demo", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "alpha" });
        Assert.Equal(1, fires);

        channels.Publish("demo", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "beta" });
        Assert.Equal(2, fires);
        channels.Publish("demo", null);
        Assert.Equal(3, fires);
        channels.Publish("demo", null);
        Assert.Equal(3, fires);
    }
    /// <summary>
    /// 跨页面接线：A 页的表格发布选中行，B 页的面板跟着变。
    ///
    /// 判据必须是**两次独立渲染**。同一次渲染里的节点表本来就通着，
    /// 那样测出来的是页内接线，跨不跨页一个字都没说。
    /// </summary>
    [Fact]
    public void SelectingARowOnOnePageDrivesAPanelOnAnother()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var table = host.Table();
                var (box, button) = host.Panel();

                // 还没选中：跟随框空着，按钮禁用且说得出为什么。
                Assert.Equal("", box.Text);
                Assert.False(button.IsEnabled);
                Assert.Contains("选中", button.ToolTip?.ToString() ?? "", StringComparison.Ordinal);

                host.Fill("2026-020-HistoryJanus", "2026-026-HistoryAurora");
                table.SelectedIndex = 1;
                UiTestHost.Pump();

                Assert.Equal("2026-026-HistoryAurora", box.Text);
                Assert.True(button.IsEnabled);

                // 选中被清掉时按钮要跟着灰回去，否则点下去用的是上一份数据里的行。
                table.SelectedIndex = -1;
                UiTestHost.Pump();
                Assert.False(button.IsEnabled);
                Assert.Equal("", box.Text);

                Assert.DoesNotContain(host.Log.Snapshot(), entry => entry.Level >= ShellLogLevel.Warn);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 点下按钮时，占位符分两个命名空间取值：<c>{selection.*}</c> 取**表格选中行**，
    /// 不带前缀的取**面板控件**。
    ///
    /// 改名这类动作要同时知道「改谁」和「改成什么」，而跟随框里的值一旦被人改过
    /// 就不再等于选中行——两者必须分别取得到，否则改名只能改成它自己。
    /// </summary>
    [Fact]
    public void ClickingTheButtonTellsTheSelectedRowApartFromTheEditedTextBox()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var table = host.Table();
                var (box, button) = host.Panel();

                host.Fill("旧名字");
                table.SelectedIndex = 0;
                UiTestHost.Pump();

                box.Text = "新 名字";
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Assert.True(
                    UiTestHost.PumpUntil(() => host.Executed.Count > 0),
                    "按钮点下去之后没有任何指令上总线");

                Assert.Equal(("旧名字", "新 名字"), host.Executed[0]);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 通道重名只认第一个声明方。后到的覆盖会让「按钮跟着哪张表走」取决于建页顺序，
    /// 而建页顺序不受任何东西保证——与动作 id 撞名走的是同一条线。
    /// </summary>
    [Fact]
    public void TheSecondTableClaimingAChannelIsRefusedOutLoud()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                host.Table();
                host.Render("another", Harness.TableJson);

                Assert.Single(host.Channels.Snapshot());
                Assert.Contains(host.Log.Snapshot(), entry =>
                    entry.Level >= ShellLogLevel.Warn
                    && entry.Message.Contains("demo.project", StringComparison.Ordinal));
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 引用了没人声明的通道必须出账。这是本机制唯一会安静失效的形态——
    /// 按钮永远灰着，与「还没选中」在界面上完全一样，只有台账能把两者分开。
    ///
    /// 判定只能在整轮建页之后做：面板所在的页可能先于表格所在的页渲染，
    /// 建时判会把正常情况报成断链。
    /// </summary>
    [Fact]
    public void APanelPointingAtANonexistentChannelShowsUpAsDangling()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                // 只建面板、不建表：通道无人声明。
                host.Panel();

                Assert.Empty(host.Channels.Snapshot());
                Assert.Contains(host.Channels.Dangling, reason =>
                    reason.Contains("demo.project", StringComparison.Ordinal));
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 纵向栈里的表格拿剩余高度，并且真的滚得动（REQ-UI-042）。
    ///
    /// 两条断言缺一不可：只断「被限住」的话，把表格设成固定高也能过；
    /// 只断「能滚」的话，撑满整页但内部恰好也滚得动的实现同样能过。
    /// </summary>
    [Fact]
    public void ATableInsideAStackGetsTheLeftoverHeightAndActuallyScrolls()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var root = host.Render("overview", """
                    {
                      "type": "stack",
                      "children": [
                        { "type": "text", "text": "项目与工作树" },
                        {
                          "type": "table",
                          "id": "projects",
                          "columns": [ { "key": "name", "title": "项目" } ]
                        }
                      ]
                    }
                    """);

                var window = host.Mount(root, 420, 240);
                var table = Descendants<AuroraTable>(root).First();
                table.SetRows(Rows(Enumerable.Range(0, 200).Select(i => "项目 " + i).ToArray()));

                for (var i = 0; i < 5; i++)
                {
                    UiTestHost.Pump();
                    window.UpdateLayout();
                }

                // 表被限在窗口里，而不是按 200 行的自然高度长出去。
                Assert.True(
                    table.ActualHeight <= window.Height,
                    $"表高 {table.ActualHeight} 超过了窗口高 {window.Height}——它没被限住");

                // 限住之后必须真的能滚：内容高大于视口高。
                Assert.Contains(Descendants<ScrollViewer>(table), scroll => scroll.ScrollableHeight > 0);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 同一声明行里的控件与按钮排在**同一个视觉行**里（REQ-UI-060）。
    ///
    /// 判据是排完版后两者的纵向区间重合，而不是"看起来在一起"：
    /// 各占一行时版面上也挨着，肉眼分不出来。
    ///
    /// 1.9.2 之前这条靠 <c>inline</c> 表达——「与前一个控件同行」。
    /// 行成为一等结构之后，同行就是同一个 <c>widgets</c> 数组，没有第二种写法。
    /// </summary>
    [Fact]
    public void ControlsInOneDeclaredRowShareOneVisualLine()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var (box, button) = host.Panel();

                var boxTop = box.TranslatePoint(new Point(0, 0), host.PanelRoot).Y;
                var buttonTop = button.TranslatePoint(new Point(0, 0), host.PanelRoot).Y;
                Assert.True(
                    Math.Abs(boxTop - buttonTop) < box.ActualHeight,
                    $"输入框顶在 {boxTop}、按钮顶在 {buttonTop}——同一声明行没有排成同一个视觉行");

                // 标签 / 输入区 / 按钮：三个元素，各占一格。
                var line = Assert.Single(Assert.Single(host.PanelBoard.CellBounds));
                Assert.Equal(3, line.Count);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>空行整份作废：一个没有小组件的行在版面上只是一段说不出来历的留白。</summary>
    [Fact]
    public void AnEmptyRowIsRejected()
    {
        var parsed = PanelDefinitionValidator.Validate(new PanelDefinition
        {
            Id = "ops",
            Title = "项目操作",
            Rows = [new PanelRow()],
        });

        Assert.False(parsed.Ok);
        Assert.Contains("空行", parsed.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取数参数引用选中行时，换一次选中就重取一次（REQ-UI-044）。
    ///
    /// 只在 Loaded 取一次的表，换选中之后显示的是上一个项目的数据——
    /// 而"过期的数据"和"新数据"在界面上长得一模一样，这是这条要挡的形态。
    /// </summary>
    [Fact]
    public void ADataSourceThatReferencesTheSelectionRefetchesWhenItChanges()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var table = host.Table();
                var detail = host.Detail();

                // 还没选中：不发指令，空表上写清为什么空。
                Assert.Empty(host.Fetched);
                Assert.Contains("选中", detail.EmptyText, StringComparison.Ordinal);

                host.Fill("alpha", "beta");
                table.SelectedIndex = 0;
                Assert.True(
                    UiTestHost.PumpUntil(() => host.Fetched.Count >= 1),
                    "选中之后没有取数");
                Assert.Equal("alpha", host.Fetched[^1]);

                table.SelectedIndex = 1;
                Assert.True(
                    UiTestHost.PumpUntil(() => host.Fetched.Count >= 2),
                    "换选中之后没有重新取数");
                Assert.Equal("beta", host.Fetched[^1]);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 连着换几次选中只取最后一次的数（REQ-UI-044 消抖）。
    ///
    /// 真机实测：在项目总览里按方向键翻项目，每一次选中变化都会同时触发
    /// 图谱、分支历史和规则落地状态三条取数，其中落地状态每次跑两条 `git ls-files`。
    /// 一秒翻五个项目就是十五条 Git 工作在排队，而其中十四条的结果没有人会看到。
    /// </summary>
    [Fact]
    public void RapidSelectionChangesCoalesceIntoOneFetch()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var table = host.Table();
                host.Detail();
                host.Fill("alpha", "beta", "gamma");

                // 连着翻三个，中间不给它取数的机会。
                table.SelectedIndex = 0;
                table.SelectedIndex = 1;
                table.SelectedIndex = 2;

                Assert.True(
                    UiTestHost.PumpUntil(() => host.Fetched.Count >= 1),
                    "消抖窗口过去之后仍然没有取数");

                // 只取一次，而且取的是**最后**那个选中——不是第一个，也不是三个都取。
                Assert.Single(host.Fetched);
                Assert.Equal("gamma", host.Fetched[0]);
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>
    /// 显式刷新按页与按节点分派，且"刷了 0 处"要与"刷新完成"分开。
    ///
    /// 规则清单、GitHub 状态这类数据在界面之外被改，界面没有任何信号知道，
    /// 只能靠一个按钮去问一次。按钮点了没反应时，差别就在这句回执上。
    /// </summary>
    [Fact]
    public void RefreshDataDispatchesByPageAndSaysSoWhenNothingMatched()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                var table = host.Table();
                host.Detail();
                host.Fill("alpha");
                table.SelectedIndex = 0;
                UiTestHost.PumpUntil(() => host.Fetched.Count >= 1);

                var before = host.Fetched.Count;
                Assert.Equal(1, host.Refresher.Refresh("detail", null));
                Assert.True(
                    UiTestHost.PumpUntil(() => host.Fetched.Count > before),
                    "按页刷新没有触发取数");

                // 别的页不该被带着一起刷：刷新是有代价的，Janus 的项目表会去扫 45 个仓。
                Assert.Equal(0, host.Refresher.Refresh("nosuchpage", null));
                Assert.Equal(1, host.Refresher.Refresh(null, "detail-rows"));
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    /// <summary>页面撤掉时取数绑定跟着撤，否则就是对着废弃控件继续发指令。</summary>
    [Fact]
    public void DroppingAnOwnerAlsoDropsItsDataBindings()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new Harness();
            try
            {
                host.Table();
                host.Detail();
                Assert.NotEmpty(host.Refresher.Snapshot());

                host.Refresher.DropOwner("HistoryDemo");

                Assert.Empty(host.Refresher.Snapshot());
                Assert.Equal(0, host.Refresher.Refresh(null, null));
            }
            finally
            {
                host.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- 装配

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(params string[] names)
        => names
            .Select(name => (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["name"] = name })
            .ToList();

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

    /// <summary>
    /// 两页一台账的最小装配。页面**分两次渲染**，与真机上两页各建一次是同一形状。
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public const string TableJson = """
            {
              "type": "table",
              "id": "projects",
              "channel": "demo.project",
              "columns": [ { "key": "name", "title": "项目" } ]
            }
            """;

        private const string PanelJson = """
            {
              "type": "panel",
              "id": "ops",
              "text": "项目操作",
              "rows": [
                {
                  "widgets": [
                    { "kind": "textbox", "id": "project-name", "label": "项目名", "flex": true,
                      "follows": "demo.project.name" },
                    { "kind": "button", "action": "demo.rename", "text": "改名",
                      "enabledWhen": { "selected": "demo.project" } }
                  ]
                }
              ]
            }
            """;

        private const string DetailJson = """
            {
              "type": "table",
              "id": "detail-rows",
              "columns": [ { "key": "name", "title": "名称" } ],
              "dataSource": {
                "command": "demo.detail",
                "args": { "name": "{selection.demo.project.name}" }
              }
            }
            """;

        private readonly List<Window> _windows = [];
        private AuroraTable? _table;

        public Harness()
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.proj.rename",
                Domain = "demo",
                CommandClass = "proj",
                Summary = "改名",
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(ctx =>
                {
                    Executed.Add((ctx.GetString("name") ?? "", ctx.GetString("new") ?? ""));
                    return CommandResult.Ok("ok");
                }),
            });

            registry.Register(new CommandDescriptor
            {
                Name = "demo.detail",
                Domain = "demo",
                CommandClass = "detail",
                Summary = "取某个项目的明细",
                Readonly = true,
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(ctx =>
                {
                    Fetched.Add(ctx.GetString("name") ?? "");
                    return CommandResult.Ok("[]");
                }),
            });

            Log = new MemoryShellLog();
            Bus = new CommandBus(registry, Log);
            Actions = new ActionRegistry(Bus, Log);
            Actions.DeclareLocal("HistoryDemo", [
                new ActionDeclaration
                {
                    Id = "demo.rename",
                    Title = "改名",
                    Command = "demo.proj.rename",
                    Args = new Dictionary<string, string>
                    {
                        ["name"] = "{selection.demo.project.name}",
                        ["new"] = "{project-name}",
                    },
                },
            ]);
            Channels = new SelectionChannels();
            Refresher = new PageDataRefresher(Channels);
        }

        public CommandBus Bus { get; }

        public MemoryShellLog Log { get; }

        public ActionRegistry Actions { get; }

        public SelectionChannels Channels { get; }

        public PageDataRefresher Refresher { get; }

        public List<(string Name, string New)> Executed { get; } = [];

        /// <summary>取数指令每被调一次记一条，值是它拿到的项目名。</summary>
        public List<string> Fetched { get; } = [];

        public AuroraTable Table()
        {
            var root = Render("overview", TableJson);
            Mount(root, 420, 240);
            _table = Descendants<AuroraTable>(root).First();
            return _table;
        }

        /// <summary>面板那一页的根，供断言两个控件排在同一个视觉行里。</summary>
        public FrameworkElement PanelRoot { get; private set; } = null!;

        /// <summary>面板的排版面，供读取版面快照。</summary>
        public AuroraPanelBoard PanelBoard { get; private set; } = null!;

        public (TextBox Box, Button Button) Panel()
        {
            var root = Render("projops", PanelJson);
            var window = Mount(root, 320, 200);
            window.UpdateLayout();
            UiTestHost.Pump();

            PanelRoot = root;
            PanelBoard = Descendants<AuroraPanelBoard>(root).First();
            return (Descendants<TextBox>(root).First(), Descendants<Button>(root).First());
        }

        /// <summary>另建一页：一张取数参数引用选中行的表。</summary>
        public AuroraTable Detail()
        {
            var root = Render("detail", DetailJson);
            Mount(root, 420, 240);
            return Descendants<AuroraTable>(root).First();
        }

        public void Fill(params string[] names)
        {
            _table!.SetRows(Rows(names));
            UiTestHost.Pump();
        }

        public FrameworkElement Render(string pageId, string contentJson)
        {
            var parsed = PageDescriptionReader.Read($$"""
                {
                  "schemaVersion": 1,
                  "owner": "HistoryDemo",
                  "pages": [ { "id": "{{pageId}}", "title": "演示", "content": {{contentJson}} } ]
                }
                """, "HistoryDemo");
            Assert.True(parsed.Ok, parsed.Error);

            var rendered = PageRenderer.Render(
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
            Assert.Empty(rendered.MissingComponents);
            return rendered.Root;
        }

        /// <summary>面板与表格都要进可视树才量得到：没进树的控件，样式与布局都还没发生。</summary>
        public Window Mount(FrameworkElement root, double width, double height)
        {
            var window = new Window
            {
                Content = root,
                Width = width,
                Height = height,
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
