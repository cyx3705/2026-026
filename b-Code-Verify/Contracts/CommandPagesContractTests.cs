using System.Text.Json;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Views;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 命令集与指令详情两页真的**建得起来、填得上数**（REQ-UI-014）。
///
/// 这两页在宿主 5.0 之后整体消失过一轮，而「消失」当时没有任何门禁能发现——
/// 它表现为主文档区空着，不是一条报错。
///
/// 1.9.0 两页改成描述式（REQ-UI-052）之后，判据跟着换了地方，但守的是同两件事：
/// **页面在**（描述解析得开、渲染不出缺件）与**数据在**（<c>aurora.ui.data</c> 真的
/// 从注册表里读得出东西）。数据这一半现在可以完全脱离 WPF 来验——那正是把取数
/// 从视图里挪进指令的附带好处。
/// </summary>
public sealed class CommandPagesContractTests
{
    [Fact]
    public async Task Data_ListsTheRegistryWithoutAnyModulePresent()
    {
        var bus = Host();
        var rows = await RowsAsync(bus, "aurora.ui.data view=commands");

        Assert.Contains(rows, row => row["name"] == "demo.branch.rename");
        Assert.Contains(rows, row => row["name"] == "demo.repo.list");
        Assert.All(rows, row =>
        {
            Assert.True(row.ContainsKey("domain"));
            Assert.True(row.ContainsKey("class"));
            Assert.True(row.ContainsKey("readonly"));
            Assert.True(row.ContainsKey("summary"));
        });
    }

    /// <summary>搜索词横跨指令名、说明与域——两级联动下拉换成的就是这一条。</summary>
    [Fact]
    public async Task Data_FiltersByNameSummaryAndDomain()
    {
        var bus = Host();

        var byName = await RowsAsync(bus, "aurora.ui.data view=commands query=branch");
        Assert.Contains(byName, row => row["name"] == "demo.branch.rename");
        Assert.DoesNotContain(byName, row => row["name"] == "demo.repo.list");

        var bySummary = await RowsAsync(bus, "aurora.ui.data view=commands query=列出仓库");
        Assert.Contains(bySummary, row => row["name"] == "demo.repo.list");

        var byDomain = await RowsAsync(bus, "aurora.ui.data view=commands query=demo");
        Assert.Equal(2, byDomain.Count(row => row["name"].StartsWith("demo.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Data_ShowsTheParameterTableForOneCommand()
    {
        var bus = Host();
        var rows = await RowsAsync(bus, "aurora.ui.data view=commandparams name=demo.branch.rename");

        // 参数表就是「这条指令收什么」的全部答案，控制台以外只有这里能看到。
        var row = Assert.Single(rows);
        Assert.Equal("to", row["name"]);
        Assert.Equal("是", row["required"]);
    }

    /// <summary>选不中任何指令时必须是**空表**，不是一条失败——那是页面刚打开的常态。</summary>
    [Fact]
    public async Task Data_ReturnsAnEmptyTableWhenNothingIsSelected()
    {
        var bus = Host();
        Assert.Empty(await RowsAsync(bus, "aurora.ui.data view=commanddetail"));
        Assert.Empty(await RowsAsync(bus, "aurora.ui.data view=commandparams"));
    }

    [Fact]
    public async Task Data_RejectsAnUnknownView()
    {
        var bus = Host();
        var result = await bus.ExecuteAsync("aurora.ui.data view=nonsense", "UI");
        Assert.False(result.Success);
    }

    /// <summary>
    /// 四页描述必须解析得开，且**渲染不出一个缺件**。
    ///
    /// 自持页出现缺件是最该被拦下的一种：它说明界面自己的协议表达不了界面自己的页面。
    /// 1.9.0 的整条论证——「组件层缺什么，界面自己先撞上」——靠的就是这条用例
    /// 在缺件出现时立刻变红，而不是等到有人看着页面觉得不对。
    /// </summary>
    [Fact]
    public void EveryHostedPageParsesAndRendersWithoutMissingComponents()
    {
        UiTestHost.RunSta(() =>
        {
            var parsed = PageDescriptionReader.Read(
                HostedPageDescriptions.Json, HostedPageDescriptions.Owner);
            Assert.True(parsed.Ok, parsed.Error);

            var ids = parsed.Value!.Pages.Select(page => page.Id).ToList();
            Assert.Equal(["mcp", "commanddetail", "modules", "components"], ids);

            var registry = new CommandRegistry();
            var log = new NullShellLog();
            var bus = new CommandBus(registry, log);
            var actions = new HistoryAurora.Shell.Actions.ActionRegistry(bus, log);
            ComponentGalleryCommands.Register(registry);
            actions.DeclareLocal(ComponentGalleryCommands.Owner, ComponentGalleryCommands.Actions);
            actions.DeclareLocal(HostedPageDescriptions.Owner, HostedPageData.Actions);

            foreach (var page in parsed.Value.Pages)
            {
                var rendered = PageRenderer.Render(page, new PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = HostedPageDescriptions.Owner,
                    Actions = actions,
                    Channels = new HistoryAurora.Shell.Selection.SelectionChannels(),
                });

                Assert.True(
                    rendered.MissingComponents.Count == 0,
                    $"{page.Id} 缺件: {string.Join("、", rendered.MissingComponents)}");
            }
        });
    }

    /// <summary>
    /// 命令集**一打开就有内容**，不需要先在搜索框里输点什么。
    ///
    /// 搜索框是**筛选**，不是**选择**：空搜索词的意思是「不筛」，而不是「还没准备好」。
    /// 取数把这两者混为一谈的话，页面一打开显示的是「请先选中一行（取数需要
    /// selection.aurora.mcp.query.value）」——一句既看不懂又没法照做的话，
    /// 因为那一格根本不是让你选行的。
    ///
    /// 这条用例钉的是判据本身：通道**发布过一行**就算有值，哪怕那一格是空串。
    /// 表格没选中时通道里压根没有行，仍然照旧判为取不到值，两者分得开。
    /// </summary>
    [Fact]
    public void TheCommandListFillsUpBeforeAnybodyTypesInTheSearchBox()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new NullShellLog();
            var bus = new CommandBus(registry, log);
            var catalog = new LocalCommandCatalogSession(bus, log);
            registry.Register(new CommandDescriptor
            {
                Name = "demo.repo.list",
                Domain = "demo",
                CommandClass = "repo",
                Summary = "列出仓库",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
            });
            HostedPageData.Register(registry, new HostedPageData.Sources
            {
                Bus = () => bus,
                Catalog = () => catalog,
            });

            var actions = new HistoryAurora.Shell.Actions.ActionRegistry(bus, log);
            actions.DeclareLocal(HostedPageDescriptions.Owner, HostedPageData.Actions);

            var parsed = PageDescriptionReader.Read(
                HostedPageDescriptions.Json, HostedPageDescriptions.Owner);
            Assert.True(parsed.Ok, parsed.Error);
            var page = parsed.Value!.Pages.Single(candidate => candidate.Id == "mcp");

            var rendered = PageRenderer.Render(page, new PageRenderContext
            {
                Bus = bus,
                Log = log,
                Owner = HostedPageDescriptions.Owner,
                Actions = actions,
                Channels = new HistoryAurora.Shell.Selection.SelectionChannels(),
            });

            var host = new System.Windows.Window
            {
                Width = 900,
                Height = 600,
                ShowActivated = false,
                ShowInTaskbar = false,
                Content = HistoryAurora.Shell.Pages.PageRegistrar.Inset(rendered.Root),
            };

            try
            {
                host.Show();
                var table = Assert.Single(Descendants<HistoryAurora.Shell.Table.AuroraTable>(host));
                var filled = UiTestHost.PumpUntil(() => table.RowCount > 0);

                Assert.True(filled, "命令集打开时是空的——搜索框还没输入就把整张表挡住了");
                Assert.Contains(table.Data.Rows, row => row.Values.Contains("demo.repo.list"));
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    /// <summary>
    /// 描述里绑的每个动作都要解析得到，且它的落点指令确实存在。
    ///
    /// 断链的形态是「按钮点了没反应」，在界面上与「还没选中」完全一样——
    /// 必须在这里判，不能等人去点。
    /// </summary>
    [Fact]
    public void EveryHostedActionResolvesToARegisteredCommand()
    {
        var registry = new CommandRegistry();
        var log = new NullShellLog();
        var bus = new CommandBus(registry, log);
        HostedPageData.Register(registry, new HostedPageData.Sources
        {
            Bus = () => bus,
            Catalog = () => null,
        });

        var actions = new HistoryAurora.Shell.Actions.ActionRegistry(bus, log);
        actions.DeclareLocal(HostedPageDescriptions.Owner, HostedPageData.Actions);

        foreach (var action in HostedPageData.Actions)
        {
            Assert.True(actions.Resolve(action.Id).Ok, action.Id);

            // vulcan.* 与内置指令组的落点不在这个裸注册表里，那不算断链。
            if (action.Command.StartsWith("vulcan.", StringComparison.OrdinalIgnoreCase)
                || action.Command is "aurora.ui.show" or "aurora.log.prefill"
                    or "aurora.command.copyexample")
                continue;
            Assert.True(registry.TryGet(action.Command, out _), action.Id + " → " + action.Command);
        }

        // 反过来也要成立：描述里写到的动作 id 必须都有声明，否则页面上是一块占位。
        var described = HostedPageDescriptions.Json;
        foreach (var id in HostedPageData.Actions.Select(a => a.Id))
            Assert.Contains("\"" + id + "\"", described, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<Dictionary<string, string>>> RowsAsync(
        CommandBus bus,
        string commandText)
    {
        var result = await bus.ExecuteAsync(commandText, "UI");
        Assert.True(result.Success, result.Message);

        var payload = result.Data as string ?? result.Message;
        return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(payload) ?? [];
    }

    private static CommandBus Host()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "demo.branch.rename",
            Domain = "demo",
            CommandClass = "branch",
            Summary = "重命名分支",
            Readonly = true,
            Parameters = [new ParameterSpec { Name = "to", Description = "新名字", Required = true }],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "demo.repo.list",
            Domain = "demo",
            CommandClass = "repo",
            Summary = "列出仓库",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });

        var log = new NullShellLog();
        var bus = new CommandBus(registry, log);
        var catalog = new LocalCommandCatalogSession(bus, log);
        HostedPageData.Register(registry, new HostedPageData.Sources
        {
            Bus = () => bus,
            Catalog = () => catalog,
        });
        return bus;
    }

    private sealed class NullShellLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message)
            => EntryAdded?.Invoke(this, new ShellLogEntry(DateTime.Now, level, category, message));

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
