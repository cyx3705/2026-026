using System.IO;
using System.Text.Json;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.HostedPages.Views;
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
            var actions = new HistoryAurora.Shell.Components.Actions.ActionRegistry(bus, log);
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
                    Channels = new HistoryAurora.Shell.Components.Selection.SelectionChannels(),
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

            var actions = new HistoryAurora.Shell.Components.Actions.ActionRegistry(bus, log);
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
                Channels = new HistoryAurora.Shell.Components.Selection.SelectionChannels(),
            });

            var host = new System.Windows.Window
            {
                Width = 900,
                Height = 600,
                ShowActivated = false,
                ShowInTaskbar = false,
                Content = HistoryAurora.Shell.Components.Pages.PageRegistrar.Inset(rendered.Root),
            };

            try
            {
                host.Show();
                var table = Assert.Single(Descendants<HistoryAurora.Shell.Components.Table.AuroraTable>(host));
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

        var actions = new HistoryAurora.Shell.Components.Actions.ActionRegistry(bus, log);
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

    /// <summary>
    /// 目录会话没接线时取数必须**失败**，不是给一张空表（REQ-UI-057）。
    ///
    /// 1.9.0 装配根漏了 <c>Catalog = _catalog</c> 这一行，而当时的写法是「拿不到会话就给空表」——
    /// 于是命令集画得好好的、表头齐全、一行数据没有，日志里也一个字都没有，
    /// 从截图上完全看不出是没接线还是真的一条指令都没有。
    /// </summary>
    [Fact]
    public async Task Data_FailsLoudlyWhenTheCatalogSessionIsMissing()
    {
        var registry = new CommandRegistry();
        var log = new NullShellLog();
        var bus = new CommandBus(registry, log);
        HostedPageData.Register(registry, new HostedPageData.Sources
        {
            Bus = () => bus,
            Catalog = () => null,
        });

        foreach (var view in new[] { "commands", "domains", "classes" })
        {
            var result = await bus.ExecuteAsync("aurora.ui.data view=" + view, "UI");
            Assert.False(result.Success, view + " 在没有目录会话时仍然报成功");
            Assert.Contains("未接线", result.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 装配根真的把目录会话接上了（REQ-UI-057）。
    ///
    /// 上一条只证明「没接线会报错」，证明不了「这台机器接上了」——而漏掉那一行
    /// 正是 1.9.0 到 1.9.1 命令集一直空着的原因。判据取源码里那一处赋值：
    /// 装配根要跑起来得有真窗口、真停靠层，在门禁里立不起来。
    /// </summary>
    [Fact]
    public void TheCompositionRootActuallyWiresTheCatalogSession()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "4-Composition", "ShellWindow.xaml.cs"));

        Assert.Contains("Catalog = _catalog,", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 命令集的列是【域 / 类 / 方法 / 只读 / 说明】，而 <c>name</c> 仍然在行里（REQ-UI-058）。
    ///
    /// 完整指令名不再占一列——它的三段就在旁边三格里。但它**必须留在行数据里**：
    /// 右键菜单的四条动作、指令详情页的两张表、以及对外契约
    /// <c>IShellCommandWorkbenchHost.CommandSelection</c> 都按它取值。
    /// 去掉一列是版面决定，去掉一个键是断链。
    /// </summary>
    [Fact]
    public async Task Data_SplitsTheCommandNameIntoDomainClassAndMethod()
    {
        var bus = Host();
        var rows = await RowsAsync(bus, "aurora.ui.data view=commands query=demo.branch");

        var row = Assert.Single(rows);
        Assert.Equal("demo.branch.rename", row["name"]);
        Assert.Equal("demo", row["domain"]);
        Assert.Equal("branch", row["class"]);
        Assert.Equal("rename", row["method"]);

        using var document = JsonDocument.Parse(HostedPageDescriptions.Json);
        var table = document.RootElement.GetProperty("pages").EnumerateArray()
            .Single(page => page.GetProperty("id").GetString() == "mcp")
            .GetProperty("content").GetProperty("children").EnumerateArray()
            .Single(child => child.GetProperty("type").GetString() == "table");

        Assert.Equal(
            new[] { "domain", "class", "method", "readonly", "summary" },
            table.GetProperty("columns").EnumerateArray()
                .Select(column => column.GetProperty("key").GetString()!)
                .ToArray());

        // 行内按钮那一列删掉，四条动作只进右键菜单（REQ-UI-058）。
        Assert.All(
            table.GetProperty("rowActions").EnumerateArray(),
            action => Assert.False(
                action.GetProperty("inline").GetBoolean(),
                "命令集不该再有行内按钮列"));
    }

    /// <summary>
    /// 方法是「去掉 <c>&lt;域&gt;.&lt;类&gt;.</c> 前缀剩下的全部」，不是「取第三段」。
    ///
    /// 取第三段会把四段名后面那截静默丢掉，于是两条只差最后一段的指令
    /// 在表上变成两行一模一样的内容。无类指令只剥域，类那一格留空。
    /// </summary>
    [Fact]
    public async Task Data_KeepsEverythingAfterTheDomainAndClassPrefix()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "demo.deep.branch.rename",
            Domain = "demo",
            CommandClass = "deep",
            Summary = "四段名",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "demo.help",
            Domain = "demo",
            Summary = "无类指令",
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

        var rows = await RowsAsync(bus, "aurora.ui.data view=commands query=demo.");

        var deep = rows.Single(row => row["name"] == "demo.deep.branch.rename");
        Assert.Equal("branch.rename", deep["method"]);

        var classless = rows.Single(row => row["name"] == "demo.help");
        Assert.Equal("", classless["class"]);
        Assert.Equal("help", classless["method"]);
    }

    /// <summary>
    /// 域与类两级联动的候选（REQ-UI-059 / DEC-021）。
    ///
    /// 域为「全部」时**不列类**：跨域列类名会把不同域里同名的类归成一条，
    /// 筛出来的结果没有人能解释。两边的首项都固定是「全部」——
    /// 选择框没有「清空」这个动作，不给一个「全部」的话筛了就退不回来。
    /// </summary>
    [Fact]
    public async Task Data_ScopesTheClassOptionsToTheChosenDomain()
    {
        var bus = Host();

        var domains = await RowsAsync(bus, "aurora.ui.data view=domains");
        Assert.Equal(HostedPageData.All, domains[0]["value"]);
        Assert.Contains(domains, row => row["value"] == "demo");

        var all = await RowsAsync(bus, "aurora.ui.data view=classes domain=" + HostedPageData.All);
        Assert.Equal(HostedPageData.All, Assert.Single(all)["value"]);

        var scoped = await RowsAsync(bus, "aurora.ui.data view=classes domain=demo");
        Assert.Equal(
            [HostedPageData.All, "branch", "repo"],
            scoped.Select(row => row["value"]).ToArray());
    }

    /// <summary>两个下拉真的在筛，而且是求交，不是互相取代。</summary>
    [Fact]
    public async Task Data_FiltersByDomainAndClassOnTopOfTheSearchWord()
    {
        var bus = Host();

        var byClass = await RowsAsync(bus, "aurora.ui.data view=commands domain=demo class=repo");
        Assert.Equal("demo.repo.list", Assert.Single(byClass)["name"]);

        // 「全部」等于不筛，与留空是同一件事。
        var unfiltered = await RowsAsync(
            bus, $"aurora.ui.data view=commands domain={HostedPageData.All} class={HostedPageData.All}");
        Assert.Equal(2, unfiltered.Count(row => row["domain"] == "demo"));

        // 搜索词与下拉叠加：demo/branch 里没有「列出仓库」。
        var intersected = await RowsAsync(
            bus, "aurora.ui.data view=commands domain=demo class=branch query=列出仓库");
        Assert.Empty(intersected);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "project.manifest.json")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
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
