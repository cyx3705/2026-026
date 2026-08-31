using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Table;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>可点击表格单元格的合同（REQ-UI-068）。</summary>
[Collection(TestCollections.Ui)]
public sealed class CellActionContractTests
{
    [Fact]
    public void CellAction_UsesButtonSemanticsAndCarriesClickedRow()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new AuroraTableColumn(
                    "folder", "z 级文件夹", "*",
                    new AuroraCellAction("demo.open", "打开文件夹"))],
                [new Dictionary<string, string> { ["folder"] = "z-notes", ["project"] = "demo" }]));

            var host = new Window
            {
                Content = table,
                Width = 500,
                Height = 240,
                ShowInTaskbar = false,
            };
            host.Show();
            UiTestHost.Pump();
            var button = Assert.Single(
                Descendants<Button>(table),
                item => Equals(item.ToolTip, "打开文件夹"));
            Assert.True(button.Focusable);

            AuroraCellActionEventArgs? captured = null;
            table.CellActionInvoked += (_, e) => captured = e;
            table.FireCell(
                "demo.open",
                "folder",
                new Dictionary<string, string> { ["folder"] = "z-notes", ["project"] = "demo" });

            Assert.NotNull(captured);
            Assert.Equal("folder", captured!.ColumnKey);
            Assert.Equal("z-notes", captured.Row["folder"]);
            Assert.Equal("demo", captured.Row["project"]);
            host.Close();
        });
    }

    [Fact]
    public void CellAction_IgnoresEmptyCellsAndUnknownActions()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new AuroraTableColumn("folder", "目录", null, new AuroraCellAction("demo.open"))],
                [new Dictionary<string, string> { ["folder"] = "" }]));
            var fired = 0;
            table.CellActionInvoked += (_, _) => fired++;

            table.FireCell("demo.open", "folder", new Dictionary<string, string> { ["folder"] = "" });
            table.FireCell("demo.gone", "folder", new Dictionary<string, string> { ["folder"] = "z-a" });

            Assert.Equal(0, fired);
        });
    }

    [Fact]
    public void PageCellAction_ResolvesRowPlaceholdersAtClickTime()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryLog();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.open",
                Domain = "demo",
                CommandClass = "ui",
                Summary = "打开",
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
            });
            registry.Register(new CommandDescriptor
            {
                Name = "demo.rows",
                Domain = "demo",
                CommandClass = "ui",
                Summary = "行",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                    "ok",
                    JsonDocument.Parse("""[{"project":"Janus","folder":"z-docs"}]""").RootElement.Clone())),
            });
            var bus = new CommandBus(registry, log);
            var actions = new ActionRegistry(bus, log);
            actions.DeclareLocal("HistoryDemo",
            [
                new ActionDeclaration
                {
                    Id = "demo.folder.open",
                    Title = "打开",
                    Command = "demo.open",
                    Args = new Dictionary<string, string>
                    {
                        ["project"] = "{project}",
                        ["folder"] = "{folder}",
                    },
                },
            ]);
            var executed = new List<string>();
            bus.Executed += (text, _, _) => executed.Add(text);

            var page = Page("""
                {
                  "type": "table",
                  "dataSource": { "command": "demo.rows" },
                  "columns": [
                    { "key": "project", "title": "项目" },
                    { "key": "folder", "title": "z 级文件夹", "cellAction": "demo.folder.open" }
                  ]
                }
                """);
            var rendered = PageRenderer.Render(page, new PageRenderContext
            {
                Bus = bus,
                Log = log,
                Owner = "HistoryDemo",
                Actions = actions,
            });
            var table = Assert.IsType<AuroraTable>(rendered.Root);
            table.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(UiTestHost.PumpUntil(() => table.RowCount == 1));

            table.FireCell("demo.folder.open", "folder", table.Data.Rows[0]);
            Assert.True(UiTestHost.PumpUntil(() => executed.Count >= 2));
            Assert.Contains("demo.open project=Janus folder=z-docs", executed);
        });
    }

    [Fact]
    public void MissingCellAction_IsVisibleAndLogged()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryLog();
            var bus = new CommandBus(registry, log);
            var rendered = PageRenderer.Render(
                Page("""
                    { "type": "table", "columns": [
                      { "key": "status", "title": "状态", "cellAction": "demo.missing" }
                    ] }
                    """),
                new PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = "HistoryDemo",
                    Actions = new ActionRegistry(bus, log),
                });

            var stack = Assert.IsType<StackPanel>(rendered.Root);
            var notice = Assert.IsType<Border>(stack.Children[0]);
            Assert.Contains("demo.missing", Assert.IsType<TextBlock>(notice.Child).Text);
            Assert.Contains(log.Snapshot(), entry => entry.Message.Contains("demo.missing"));
        });
    }

    private static PageDescription Page(string content)
    {
        var parsed = PageDescriptionReader.Read($$"""
            { "schemaVersion": 1, "owner": "HistoryDemo", "pages": [
              { "id": "demo", "title": "演示", "content": {{content}} }
            ] }
            """, "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    private static GridView GridView(AuroraTable table)
    {
        var surface = Assert.IsType<Border>(table.Content);
        var grid = Assert.IsType<Grid>(surface.Child);
        return Assert.IsType<GridView>(grid.Children.OfType<ListView>().Single().View);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
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
