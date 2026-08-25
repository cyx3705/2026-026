using System.Windows.Controls;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>补全输入框与响应式栅格在页面协议里的落点，外加本文件族共用的装配。</summary>
public sealed partial class PageComponentsContractTests
{
    [Fact]
    public void Input_FallsBackToAPlainBoxWhenThereIsNoCompletionSession()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();

            var rendered = PageRenderer.Render(
                Page("""{ "type": "input", "suggest": "commands" }"""),
                new PageRenderContext { Bus = bus, Log = log, Owner = "HistoryDemo", Actions = actions });

            Assert.IsType<TextBox>(rendered.Root);
            // 静默地少掉补全只会被当成手感问题，必须留痕。
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level == ShellLogLevel.Warn && entry.Message.Contains("suggest"));
        });
    }

    [Fact]
    public void Input_UsesTheSuggestBoxWhenASessionIsAvailable()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host();

            var rendered = PageRenderer.Render(
                Page("""{ "type": "input", "suggest": "commands" }"""),
                new PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = "HistoryDemo",
                    Actions = actions,
                    Completions = (_, _, _) => Task.FromResult(ConsoleCompletionResult.Empty),
                });

            Assert.IsType<AuroraSuggestBox>(rendered.Root);
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
