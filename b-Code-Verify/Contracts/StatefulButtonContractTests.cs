using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Panels;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class StatefulButtonContractTests
{
    [Fact]
    public void PanelButtonKeepsUiResponsiveDuringSynchronousCommandAndWaitsForRefresh()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryShellLog();
            var bus = new CommandBus(registry, log);
            using var release = new ManualResetEventSlim(false);
            var fetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            registry.Register(new CommandDescriptor
            {
                Name = "demo.run",
                Domain = "demo",
                CommandClass = "run",
                Summary = "run",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ =>
                {
                    Interlocked.Increment(ref calls);
                    if (!release.Wait(5000)) throw new TimeoutException();
                    return CommandResult.Ok("done");
                }),
            });
            var actions = new ActionRegistry(bus, log);
            actions.DeclareLocal("demo", [new ActionDeclaration { Id = "run", Title = "执行", Command = "demo.run" }]);
            var refresher = new PageDataRefresher();
            refresher.Register("demo", "test", "rows", [], () => fetch.Task);
            var panel = new PanelView(new PanelDefinition
            {
                Id = "test",
                Title = "test",
                Rows = [new PanelRow { Widgets = [new PanelWidget { Kind = "button", Text = "执行", Action = "run" }] }],
            }, bus, log, actions, null, "demo", refresher, "test");
            var window = new Window { Content = panel, Width = 500, Height = 240, ShowInTaskbar = false };
            try
            {
                window.Show(); UiTestHost.Pump();
                var button = Descendants<Button>(panel).Single();
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(UiTestHost.PumpUntil(() => Volatile.Read(ref calls) == 1));
                var activity = AuroraCommandActivity.GetActivity(button)!;
                Assert.True(activity.IsRunning);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, calls);
                release.Set(); UiTestHost.Pump();
                Assert.True(activity.IsRunning);
                fetch.SetResult();
                Assert.True(UiTestHost.PumpUntil(() => !activity.IsRunning));
                Assert.Equal("已完成", activity.Status);
            }
            finally { release.Set(); fetch.TrySetResult(); window.Close(); }
        });
    }

    [Fact]
    public void CellButtonShowsProgressPreventsRepeatAndRecoversAfterFailure()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create([new("name", "名称", "*", new("run"))],
                [new Dictionary<string, string> { ["name"] = "执行" }]));
            var window = new Window { Content = table, Width = 500, Height = 240, ShowInTaskbar = false };
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            table.CellActionInvoked += (_, e) => { calls++; e.Completion = completion.Task; };
            try
            {
                window.Show(); UiTestHost.Pump();
                var button = Descendants<Button>(table).Single();
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                UiTestHost.Pump();
                var activity = AuroraCommandActivity.GetActivity(button)!;
                Assert.True(activity.IsRunning);
                Assert.Equal(1, calls);
                Assert.Contains(Descendants<ProgressBar>(button), bar => bar.IsVisible && bar.IsIndeterminate);
                Assert.False(button.IsHitTestVisible);
                var originalRow = button.DataContext;
                button.DataContext = new Dictionary<string, string> { ["name"] = "另一行" };
                Assert.False(AuroraCommandActivity.GetActivity(button)!.IsRunning);
                button.DataContext = originalRow;
                Assert.Same(activity, AuroraCommandActivity.GetActivity(button));
                completion.SetException(new InvalidOperationException("test failure"));
                Assert.True(UiTestHost.PumpUntil(() => !activity.IsRunning));
                Assert.Contains("test failure", activity.Status);
                Assert.True(button.IsHitTestVisible);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(2, calls);
                completion.SetResult(true);
                Assert.True(UiTestHost.PumpUntil(() => !activity.IsRunning));
                Assert.Equal("已完成", activity.Status);
            }
            finally { completion.TrySetResult(false); window.Close(); }
        });
    }

    [Theory]
    [InlineData(450)]
    [InlineData(1100)]
    public void RuleTablesInResponsiveGridHaveBoundedScrollableViewports(double width)
    {
        UiTestHost.RunSta(() =>
        {
            var log = new MemoryShellLog();
            var root = PageRenderer.Render(new PageDescription
            {
                Id = "rules",
                Content = new PageNode
                {
                    Type = "stack",
                    Children = [new PageNode { Type = "text", Text = "Git 文件规则" },
                        new PageNode { Type = "grid", Min = 320, Children = [new PageNode { Type = "table" }, new PageNode { Type = "table" }] }],
                },
            }, new PageRenderContext { Owner = "demo", Bus = new CommandBus(new CommandRegistry(), log), Log = log }).Root;
            var window = new Window { Content = root, Width = width, Height = 480, ShowInTaskbar = false };
            try
            {
                window.Show(); UiTestHost.Pump();
                var tables = Descendants<AuroraTable>(root).ToArray();
                Assert.Equal(2, tables.Length);
                foreach (var table in tables)
                    table.SetRows(Enumerable.Range(0, 500).Select(i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["rule"] = "rule-" + i }).ToArray());
                window.UpdateLayout(); UiTestHost.Pump();
                foreach (var table in tables)
                {
                    Assert.InRange(table.ActualHeight, 60, 470);
                    var viewer = Descendants<ScrollViewer>(table).First();
                    Assert.True(viewer.ScrollableHeight > 0);
                    viewer.ScrollToEnd(); window.UpdateLayout(); UiTestHost.Pump();
                    Assert.True(viewer.VerticalOffset > 0);
                    Assert.InRange(Descendants<ListViewItem>(table).Count(), 1, 80);
                }
            }
            finally { window.Close(); }
        });
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
