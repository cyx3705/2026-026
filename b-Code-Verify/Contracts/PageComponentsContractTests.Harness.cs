using System.Windows.Controls;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>补全输入框与响应式栅格在页面协议里的落点，外加本文件族共用的装配。</summary>
public sealed partial class PageComponentsContractTests
{
    /// <summary>
    /// 退役的三种节点必须被拒绝，而且**不能记成缺件**。
    ///
    /// 缺件会被 ModulePageLoader 自动记进组件申请台账（「用出来的申请更可信」），
    /// 退役类型走那条路等于让模块不断申请一个已经决定不给的东西。
    /// </summary>
    [Theory]
    [InlineData("button")]
    [InlineData("input")]
    [InlineData("select")]
    public void RetiredNodeTypes_AreRejectedWithoutBecomingAComponentRequest(string type)
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();

            var rendered = PageRenderer.Render(
                Page($$"""{ "type": "{{type}}", "text": "x" }"""),
                new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });

            Assert.Empty(rendered.MissingComponents);
            Assert.DoesNotContain(type, PageRenderer.SupportedComponents);
            Assert.Contains(type, (System.Collections.Generic.IDictionary<string, string>)PageRenderer.RetiredComponents);

            // 拒绝要看得见：界面上是一块写着原因的牌子，日志里也有一条。
            var box = Assert.IsType<Border>(rendered.Root);
            var caption = Assert.IsType<TextBlock>(box.Child);
            Assert.Contains("控制面板", caption.Text, StringComparison.Ordinal);
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level >= ShellLogLevel.Warn && entry.Message.Contains("控制面板", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Grid_TakesOnlyAMinimumColumnWidth()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();

            var rendered = PageRenderer.Render(
                Page("""
                    {
                      "type": "grid",
                      "min": 180,
                      "children": [ { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ]
                    }
                    """),
                new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });

            var grid = Assert.IsType<AuroraGridPanel>(rendered.Root);
            Assert.Equal(180, grid.MinColumnWidth);
            Assert.Equal(2, grid.Children.Count);
        });
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
        var parsed = PageDescriptionReader.Read($$"""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [ { "id": "demo", "title": "演示", "content": {{contentJson}} } ]
            }
            """, "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    private static (CommandBus Bus, MemoryLog Log, ActionRegistry Actions) Host()
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
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                {
                  "schemaVersion": 1,
                  "owner": "HistoryDemo",
                  "actions": [
                    { "id": "demo.pin", "title": "固定", "command": "demo.item.pin",
                      "args": { "name": "{name}" } }
                  ]
                }
                """)),
        });

        var log = new MemoryLog();
        var bus = new CommandBus(registry, log);
        return (bus, log, new ActionRegistry(bus, log));
    }

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
