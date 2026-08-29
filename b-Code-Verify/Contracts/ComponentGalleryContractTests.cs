using System.Text.Json;
using System.Windows;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.Logging;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Views;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class ComponentGalleryContractTests
{
    [Fact]
    public void DescriptionCoversEveryPageComponentAndKeepsTheProtocolVersion()
    {
        var parsed = PageDescriptionReader.Read(
            HostedPageDescriptions.Json,
            ComponentGalleryCommands.Owner);

        Assert.True(parsed.Ok, parsed.Error);
        Assert.Equal(1, parsed.Value!.SchemaVersion);

        // 1.9.0 起自持描述里有四页，组件测试只是其中一页——**按 id 找，不按下标**。
        // 按下标取会在下一次调整页序时静默换成另一页，而断言仍然全绿。
        using var document = JsonDocument.Parse(HostedPageDescriptions.Json);
        var types = document.RootElement
            .GetProperty("pages")
            .EnumerateArray()
            .Single(page => page.GetProperty("id").GetString() == "components")
            .GetProperty("content")
            .ToString();

        foreach (var type in PageRenderer.SupportedComponents)
        {
            // 小型交互控件只能出现在控制面板中；页面节点只保留复合容器和展示组件。
            if (type is "button" or "input" or "select")
                continue;
            Assert.Contains("\"type\":\"" + type + "\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        // 面板的两种行模式都要在这一页上出现（REQ-UI-060）：
        // 组件测试页是"组件能表达什么"的唯一目录，缺席的能力没人会先撞上。
        Assert.Contains("\"mode\":\"even\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"flex\":true", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\":\"button\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\":\"textbox\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"mode\":\"select\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 组件测试页整页渲染：按钮全部绑上动作，取数指令全部命中。
    ///
    /// 这条用例守的是 1.8.9 的两处实测故障：
    /// 页面上每个按钮都是「未声明的动作: preview.apply」，泳道那条则是
    /// `✗ 未知指令: aurora.preview.graph`。两者都不会让渲染抛异常——
    /// 页面照样画出来，只是画的是一排警示牌，所以必须按**日志**判定。
    /// </summary>
    [Fact]
    public void GalleryBindsEveryActionAndResolvesEveryDataCommand()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryShellLog();
            var bus = new CommandBus(registry, log);
            var actions = new ActionRegistry(bus, log);

            ComponentGalleryCommands.Register(registry);
            actions.DeclareLocal(ComponentGalleryCommands.Owner, ComponentGalleryCommands.Actions);

            // 通道台账必须真的传进去：漏传的症状不是崩，而是「表格声明的 channel 已忽略」
            // 一条 Warn，随后跟随框与按钮启停全部不生效——正好是这条用例要挡的形态。
            var channels = new SelectionChannels();
            // 页面由描述渲染，与模块页同一条路——测试里也不许有第二条。
            var parsed = PageDescriptionReader.Read(
                HostedPageDescriptions.Json, ComponentGalleryCommands.Owner);
            Assert.True(parsed.Ok, parsed.Error);
            var page = parsed.Value!.Pages.Single(p => p.Id == "components");

            var rendered = PageRenderer.Render(page, new PageRenderContext
            {
                Bus = bus,
                Log = log,
                Owner = ComponentGalleryCommands.Owner,
                Actions = actions,
                Channels = channels,
            });
            Assert.Empty(rendered.MissingComponents);

            var host = new Window
            {
                Width = 900,
                Height = 700,
                ShowActivated = false,
                Content = PageRegistrar.Inset(rendered.Root),
            };
            host.Show();

            // 取数走 Loaded → 后台派发 → 线程池，两条数据源都回来了才算画完。
            var loaded = UiTestHost.PumpUntil(() => log.Snapshot()
                .Count(entry => entry.Message.Contains("aurora.preview.", StringComparison.Ordinal)) >= 2);
            host.Close();
            Assert.True(loaded, "组件测试页的取数指令没有在超时前跑完");

            var complaints = log.Snapshot()
                .Where(entry => entry.Level >= ShellLogLevel.Warn)
                .Select(entry => entry.Message)
                .ToList();

            Assert.True(complaints.Count == 0, string.Join(" | ", complaints));
        });
    }

    /// <summary>
    /// 取数指令必须出现在 <b>Attach 那一刻</b>的前端指令快照里。
    ///
    /// 这是 1.8.10 实测踩到的不变量：界面总线默认把命令发给宿主
    /// （<c>AuroraShellHost.WireBuses</c> 只把本机已登记且 <c>RequiresUiThread</c> 的留在界面侧），
    /// 而宿主注册表只收 <c>PublishShellCommands</c> 在 Attach 时抄过去的那一批。
    /// 1.8.10 把这批指令挪到「页面打开前登记」，于是它们永远进不了宿主注册表——
    /// 症状是 `✗ 未知指令: aurora.preview.rows`，而进程内的用例全绿。
    ///
    /// 因此判据不能是「注册表里有」，必须是「**框架快照里有**」。
    /// </summary>
    [Fact]
    public void DataCommandsAreInTheSnapshotThatGetsPublishedToTheHost()
    {
        var published = HistoryAurora.Shell.FrontendCommandCatalog.FrameworkSourceDescriptors
            .Select(descriptor => descriptor.Name)
            .ToList();

        Assert.Contains("aurora.preview.rows", published);
        Assert.Contains("aurora.preview.graph", published);
        Assert.Contains("aurora.preview.echo", published);
    }

    /// <summary>
    /// 本机有、宿主没有的命令必须就地执行。
    ///
    /// 这是 1.8.9～1.8.12 连查四轮的那条实测故障：`aurora.preview.rows` / `.graph`
    /// 在界面注册表里明明在，一执行却报 `✗ 未知指令`——因为界面总线默认把命令发给宿主，
    /// 而它们没能进宿主注册表。为什么没进去是宿主那一侧的事；
    /// **不管为什么，把一条本机能跑的命令发出去换回「不认识」都是错的。**
    /// </summary>
    [Fact]
    public void CommandsTheHostDoesNotHaveStayLocal()
    {
        var local = new CommandRegistry();
        ComponentGalleryCommands.Register(local);
        var host = new CommandRegistry();

        Assert.False(HistoryAurora.Module.AuroraShellHost.ShouldUseRemote(
            local, host, "aurora.preview.graph", "UI"));
        Assert.False(HistoryAurora.Module.AuroraShellHost.ShouldUseRemote(
            local, host, "aurora.preview.rows", "UI"));

        // 宿主也有的时候维持原状：仍旧发过去，由宿主作为唯一目录。
        ComponentGalleryCommands.Register(host);
        Assert.True(HistoryAurora.Module.AuroraShellHost.ShouldUseRemote(
            local, host, "aurora.preview.graph", "UI"));

        // 本机压根没有的仍旧发给宿主，否则模块命令就没人接了。
        Assert.True(HistoryAurora.Module.AuroraShellHost.ShouldUseRemote(
            local, host, "janus.branch.list", "UI"));
    }

    /// <summary>登记两遍不得抛：页面每次打开都会调一次。</summary>
    [Fact]
    public void RegisteringTheGalleryCommandsTwiceIsANoOp()
    {
        var registry = new CommandRegistry();
        ComponentGalleryCommands.Register(registry);
        ComponentGalleryCommands.Register(registry);

        Assert.True(registry.TryGet("aurora.preview.graph", out _));
        Assert.True(registry.TryGet("aurora.preview.rows", out _));
        Assert.True(registry.TryGet("aurora.preview.echo", out _));
    }

    /// <summary>
    /// 界面自带的动作不走 <c>&lt;域&gt;.ui.actions</c> 拉取，因此模块重载清表时不能被清掉。
    /// </summary>
    [Fact]
    public async Task LocalActionsSurviveAModuleReload()
    {
        var registry = new CommandRegistry();
        var log = new MemoryShellLog();
        var actions = new ActionRegistry(new CommandBus(registry, log), log);

        ComponentGalleryCommands.Register(registry);
        actions.DeclareLocal(ComponentGalleryCommands.Owner, ComponentGalleryCommands.Actions);
        await actions.ReloadAsync();

        Assert.True(actions.Resolve("preview.apply").Ok);
    }
}
