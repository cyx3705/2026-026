using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class TableUpdateContractTests
{
    [Fact]
    public void RendererUsesRevisionCoalescesRefreshAndKeepsRowsOnFailure()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryShellLog();
            var bus = new CommandBus(registry, log);
            var refresher = new PageDataRefresher();
            var calls = 0;
            var seenRevision = "";
            using var release = new ManualResetEventSlim(false);
            registry.Register(new CommandDescriptor
            {
                Name = "demo.rows",
                Domain = "demo",
                CommandClass = "rows",
                Summary = "rows",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""{"mode":"snapshot","revision":"r1","rows":[{"id":"a","note":"old"}]}""")),
            });
            registry.Register(new CommandDescriptor
            {
                Name = "demo.delta",
                Domain = "demo",
                CommandClass = "delta",
                Summary = "delta",
                Readonly = true,
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(ctx =>
                {
                    seenRevision = ctx.GetString("since") ?? "";
                    var call = Interlocked.Increment(ref calls);
                    if (call == 1 && !release.Wait(5000)) throw new TimeoutException();
                    return call <= 2
                        ? CommandResult.Ok("""{"mode":"delta","revision":"r2","upserts":[{"id":"a","note":"new"}],"removes":[]}""")
                        : CommandResult.Fail("offline");
                }),
            });
            var root = PageRenderer.Render(new PageDescription
            {
                Id = "test",
                Content = new PageNode
                {
                    Type = "table",
                    Id = "rows",
                    DataSource = new PageDataSource { Command = "demo.rows", DeltaCommand = "demo.delta", RowKey = "id" },
                },
            }, new PageRenderContext { Owner = "demo", Bus = bus, Log = log, Refresher = refresher }).Root;
            var window = new Window { Content = root, Width = 600, Height = 320, ShowInTaskbar = false };
            try
            {
                window.Show();
                var table = root as AuroraTable ?? Descendants<AuroraTable>(root).Single();
                Assert.True(UiTestHost.PumpUntil(() => table.RowCount == 1));
                refresher.Refresh("test", "rows");
                Assert.True(UiTestHost.PumpUntil(() => Volatile.Read(ref calls) == 1));
                for (var i = 0; i < 20; i++) refresher.Refresh("test", "rows");
                Assert.Equal("old", table.Data.Rows[0]["note"]);
                Assert.Contains(Descendants<TextBlock>(table), text => text.Text.Contains("正在刷新"));
                release.Set();
                Assert.True(UiTestHost.PumpUntil(() => table.Data.Rows[0]["note"] == "new", 10000), string.Join(" | ", Descendants<TextBlock>(table).Select(text => text.Text)) + " calls=" + calls);
                Assert.Equal(2, calls);
                Assert.Equal("r1", seenRevision);
                refresher.Refresh("test", "rows");
                Assert.True(UiTestHost.PumpUntil(() => Descendants<TextBlock>(table).Any(text => text.Text.Contains("刷新失败"))));
                Assert.Equal("r2", seenRevision);
                Assert.Equal("new", table.Data.Rows[0]["note"]);
            }
            finally { release.Set(); window.Close(); }
        });
    }

    [Fact]
    public void DeltaChangesOnlyAffectedRowsAndRejectsInvalidBatchAtomically()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetRows([Row("a", "one"), Row("b", "two"), Row("c", "three")]);
            var grid = (Grid)((Border)table.Content).Child;
            var list = grid.Children.OfType<ListView>().Single();
            var view = (GridView)list.View;
            var columns = view.Columns.ToArray();
            var untouched = list.Items[2];
            var events = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)list.ItemsSource).CollectionChanged += (_, e) => events.Add(e.Action);
            table.SelectedIndex = 1;
            table.ApplyDelta("id", [new Dictionary<string, string> { ["id"] = "b", ["note"] = "changed" }, Row("d", "four")], ["a"]);
            Assert.Equal("changed", table.SelectedRow!["note"]);
            Assert.Equal("b", table.SelectedRow["id"]);
            Assert.Same(untouched, list.Items[1]);
            Assert.Equal(columns, view.Columns.ToArray());
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
            Assert.Equal(3, events.Count);
            var before = table.Data;
            Assert.Throws<ArgumentException>(() => table.ApplyDelta("id", [Row("b", "bad"), Row("b", "duplicate")]));
            Assert.Same(before, table.Data);
            table.ShowStatus("正在刷新");
            Assert.Contains("正在刷新 · 3 条 · 更新时间：", grid.Children.OfType<TextBlock>().Last().Text);
            Assert.Equal("changed", table.SelectedRow["note"]);
        });
    }

    [Fact]
    public void LargeTableRecyclesVisibleRowsAndReusesIdenticalSnapshot()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            var data = AuroraTableData.FromRows(Enumerable.Range(0, 10000).Select(i => Row(i.ToString(), "value")).ToList());
            table.SetData(data);
            var window = new Window { Content = table, Width = 700, Height = 320, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                UiTestHost.Pump();
                var list = Descendants<ListView>(table).Single();
                var before = list.Items[0];
                var firstColumn = ((GridView)list.View).Columns[0];
                var changes = 0;
                ((INotifyCollectionChanged)list.ItemsSource).CollectionChanged += (_, _) => changes++;
                table.SetData(data);
                Assert.Equal(0, changes);
                Assert.Same(before, list.Items[0]);
                Assert.Same(firstColumn, ((GridView)list.View).Columns[0]);
                Assert.InRange(Descendants<ListViewItem>(table).Count(), 1, 100);
                var scroll = Descendants<ScrollViewer>(table).First();
                scroll.ScrollToEnd();
                window.UpdateLayout();
                UiTestHost.Pump();
                Assert.True(scroll.VerticalOffset > 0);
                Assert.InRange(Descendants<ListViewItem>(table).Count(), 1, 100);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ProtocolAcceptsSnapshotsAndDeltasAndRejectsDuplicateKeys()
    {
        var snapshot = TableUpdate.Read("""{"mode":"snapshot","revision":"r1","rows":[{"id":"a"}]}""");
        snapshot.ValidateKeys("id");
        Assert.False(snapshot.IsDelta);
        var delta = TableUpdate.Read("""{"mode":"delta","revision":"r2","upserts":[{"id":"a","note":"new"}],"removes":["b"]}""");
        Assert.True(delta.IsDelta);
        Assert.Equal("b", Assert.Single(delta.Removes));
        Assert.Throws<InvalidOperationException>(() => TableUpdate.Read("""[{"id":"a"},{"id":"a"}]""").ValidateKeys("id"));
        Assert.Throws<InvalidOperationException>(() => TableUpdate.Read("[null]"));
    }

    private static IReadOnlyDictionary<string, string> Row(string id, string note)
        => new Dictionary<string, string> { ["id"] = id, ["note"] = note };

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}


