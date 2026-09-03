using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 1.7.0 新交付的三个组件在页面协议里的契约：行操作、弹出层、响应式栅格
/// （REQ-UI-011 / 012 / 015），外加输入框的补全开关（REQ-UI-013）。
///
/// 每一条都对着「前端组件缺失需求书」里的一条绕法：绕法本身能用，代价是
/// 多一次来回移动、多占一屏版面、或者窄窗口下横向溢出。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed partial class PageComponentsContractTests
{
    [Fact]
    public void NewComponentsAreNoLongerRenderedAsPlaceholders()
    {
        UiTestHost.RunSta(() =>
        {
            foreach (var type in new[] { "grid", "popup" })
            {
                var rendered = Render($$"""{ "type": "{{type}}" }""");
                Assert.Empty(rendered.MissingComponents);
            }
        });
    }

    [Fact]
    public void RequestedCapabilitiesCountAsDelivered()
    {
        // 台账按名字销账。Mercury 申请的是 table.rowactions / menu / input.suggest 这些**能力**名，
        // 它们不是节点类型，因此单独一份清单——混进组件清单会让
        // type:"table.rowactions" 一边判为已支持、一边渲染成缺件占位。
        Assert.Contains("table.rowactions", PageRenderer.SupportedCapabilities);
        Assert.Contains("table.cellaction", PageRenderer.SupportedCapabilities);
        Assert.Contains("dialog.choice", PageRenderer.SupportedCapabilities);
        Assert.Contains("panel.icon", PageRenderer.SupportedCapabilities);
        Assert.Contains("menu", PageRenderer.SupportedCapabilities);
        Assert.DoesNotContain("table.rowactions", PageRenderer.SupportedComponents);

        // input.suggest 随 input 节点一同退役：能力表里再留着，模块会以为还能申请。
        Assert.DoesNotContain("input.suggest", PageRenderer.SupportedCapabilities);
    }

    [Fact]
    public void RowAction_SendsTheClickedRowIntoTheDeclaredCommand()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();
            actions.ReloadAsync().GetAwaiter().GetResult();

            var executed = new List<string>();
            bus.Executed += (text, _, _) => executed.Add(text);

            var rendered = PageRenderer.Render(
                Page("""
                    {
                      "type": "table",
                      "id": "items",
                      "columns": [ { "key": "name", "title": "名称" } ],
                      "rowActions": [ { "action": "demo.pin", "title": "固定" } ]
                    }
                    """),
                new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });

            var table = Assert.IsType<AuroraTable>(rendered.Root);
            table.Fire("demo.pin", new Dictionary<string, string> { ["name"] = "alpha" });
            UiTestHost.PumpUntil(() => executed.Count > 0);

            // 占位符 {name} 默认取**被点那一行**的同名列——行操作的作用域天然就是一行。
            Assert.Equal("demo.item.pin name=alpha", Assert.Single(executed));
        });
    }

    [Fact]
    public void RowAction_WithoutADeclarationSaysSoAboveTheTable()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();

            var rendered = PageRenderer.Render(
                Page("""
                    {
                      "type": "table",
                      "columns": [ { "key": "name", "title": "名称" } ],
                      "rowActions": [ { "action": "demo.gone", "title": "固定" } ]
                    }
                    """),
                new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });

            // 表照画，断链写在它上面：少一个按钮是看不出来的。
            var stack = Assert.IsType<StackPanel>(rendered.Root);
            Assert.IsType<Border>(stack.Children[0]);
            var table = Assert.IsType<AuroraTable>(stack.Children[1]);
            Assert.Empty(table.RowActions);
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level == ShellLogLevel.Warn && entry.Message.Contains("demo.gone"));
        });
    }
}
