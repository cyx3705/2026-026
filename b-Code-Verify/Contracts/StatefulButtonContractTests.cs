using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
                Assert.Contains(Descendants<TextBlock>(button), text => text.IsVisible && text.Text == "运行中");
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
                Assert.Contains(Descendants<TextBlock>(button), text => text.IsVisible && text.Text == "运行中");
                Assert.Equal(1, calls);
                Assert.Contains(Descendants<ProgressBar>(button), bar => bar.IsVisible && bar.IsIndeterminate);
                var progress = Descendants<ProgressBar>(button).Single();
                Assert.InRange(progress.ActualWidth, button.ActualWidth - 4, button.ActualWidth);
                Assert.InRange(progress.ActualHeight, button.ActualHeight - 4, button.ActualHeight);
                Assert.True(Descendants<System.Windows.Shapes.Rectangle>(progress).Single().HasAnimatedProperties);
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

    /// <summary>
    /// REQ-UI-124：运行态经**框架自带**的 AutomationProperties.ItemStatus 到达按钮，
    /// 按钮模板里不得再出现本程序集的类型。
    ///
    /// 这条是真机缺陷的回归闸：模板此前用 <c>(actions:AuroraCommandActivity.Activity).IsRunning</c>
    /// 反向取值，而模板可能由 WPF 跨热重载缓存成旧 ALC 的属性身份——门禁跑在默认 ALC 上，
    /// 永远复现不出来（与 REQ-UI-025 同一类只在真机成立的缺陷）。能在门禁里钉住的，
    /// 正是"模板不许依赖本程序集的附加属性"这条结构性约束。
    /// </summary>
    [Fact]
    public void ButtonTemplatesTakeRunningStateFromFrameworkOwnedItemStatusOnly()
    {
        UiTestHost.RunSta(() =>
        {
            var controls = new ResourceDictionary
            {
                Source = new Uri("/HistoryAurora;component/Themes/AuroraControls.xaml", UriKind.Relative),
            };

            foreach (var key in new[] { "Aurora.Button.Base", "Aurora.Panel.Switch" })
            {
                var style = Assert.IsType<Style>(controls[key]);

                foreach (var setter in style.Setters.OfType<Setter>())
                    Assert.NotEqual(AuroraCommandActivity.ActivityProperty, setter.Property);

                var template = Assert.IsType<ControlTemplate>(style.Setters
                    .OfType<Setter>()
                    .Single(setter => setter.Property == Control.TemplateProperty)
                    .Value);

                // 旧形态：DataTrigger 顺着本程序集的附加属性往下走。一条都不许剩。
                foreach (var trigger in template.Triggers.OfType<DataTrigger>())
                {
                    var path = (trigger.Binding as Binding)?.Path?.Path ?? "";
                    Assert.DoesNotContain(nameof(AuroraCommandActivity), path, StringComparison.Ordinal);
                }

                var running = Assert.Single(
                    template.Triggers.OfType<Trigger>(),
                    trigger => trigger.Property == AutomationProperties.ItemStatusProperty);
                Assert.Equal(AuroraCommandActivity.RunningStatus, running.Value);
                Assert.Contains(running.Setters.OfType<Setter>(), setter =>
                    setter.TargetName == "RunningProgress"
                    && setter.Property == UIElement.VisibilityProperty
                    && (Visibility)setter.Value! == Visibility.Visible);
            }
        });
    }

    /// <summary>
    /// 状态是**推**到按钮身上的：挂上、进入运行态、退出运行态各写一次 ItemStatus，
    /// 改挂另一份状态时旧那份不得继续写（虚拟化换行后"运行中"留在别人行上的形态）。
    /// </summary>
    [Fact]
    public void ActivityPushesItemStatusOntoEveryAttachedElementAndReleasesReplacedOnes()
    {
        UiTestHost.RunSta(() =>
        {
            var activity = new AuroraCommandActivity();
            var first = new Button();
            var second = new Button();
            AuroraCommandActivity.SetActivity(first, activity);
            AuroraCommandActivity.SetActivity(second, activity);
            Assert.Equal("就绪", AutomationProperties.GetItemStatus(first));

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = activity.RunAsync(() => gate.Task);
            Assert.Equal(AuroraCommandActivity.RunningStatus, AutomationProperties.GetItemStatus(first));
            Assert.Equal(AuroraCommandActivity.RunningStatus, AutomationProperties.GetItemStatus(second));

            // 换挂：second 交给另一份状态，旧那份必须当场把它放回空闲。
            var other = new AuroraCommandActivity();
            AuroraCommandActivity.SetActivity(second, other);
            Assert.Equal("就绪", AutomationProperties.GetItemStatus(second));

            gate.SetResult(true);
            Assert.True(UiTestHost.PumpUntil(() => !activity.IsRunning));
            Assert.True(UiTestHost.PumpUntil(
                () => AutomationProperties.GetItemStatus(first) == "已完成"));
            Assert.Equal("就绪", AutomationProperties.GetItemStatus(second));
            run.GetAwaiter().GetResult();
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
