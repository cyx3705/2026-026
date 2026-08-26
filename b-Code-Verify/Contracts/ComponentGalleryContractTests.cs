using System.Text.Json;
using System.Windows;
using HistoryAurora.Shell.Actions;
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
            HistoryAurora.Shell.Views.ComponentGalleryDescription.Json,
            HistoryAurora.Shell.Views.ComponentGalleryCommands.Owner);

        Assert.True(parsed.Ok, parsed.Error);
        Assert.Equal(1, parsed.Value!.SchemaVersion);

        using var document = JsonDocument.Parse(HistoryAurora.Shell.Views.ComponentGalleryDescription.Json);
        var types = document.RootElement
            .GetProperty("pages")[0]
            .GetProperty("content")
            .ToString();

        foreach (var type in PageRenderer.SupportedComponents)
        {
            // 小型交互控件只能出现在控制面板中；页面节点只保留复合容器和展示组件。
            if (type is "button" or "input" or "select")
                continue;
            Assert.Contains("\"type\":\"" + type + "\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("\"orientation\":\"horizontal\"", types.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
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

            var view = new ComponentGalleryView(bus, log, actions, null!);
            var host = new Window { Width = 900, Height = 700, ShowActivated = false, Content = view };
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
