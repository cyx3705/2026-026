using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HistoryAurora.Shell.Components.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>REQ-UI-157：保存必须使用正在编辑的行，刷新或选择变化不能触发另一项目的写入。</summary>
[Collection(TestCollections.Ui)]
public sealed class CellEditContractTests
{
    [Fact]
    public void RenderedTableOnlyEditsEligibleRowsAndEnterSubmits()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new("item", "项", "120"), new("value", "值", "*", CellEditAction: new("demo.save"), EditableWhen: "editable")],
                [new Dictionary<string, string> { ["item"] = "远端地址", ["value"] = "https://github.com/example/project", ["editable"] = "true" },
                 new Dictionary<string, string> { ["item"] = "连接状态", ["value"] = "已连接" }]));
            var host = new Window { Content = table, Width = 680, Height = 240, ShowInTaskbar = false };
            host.Show();
            UiTestHost.Pump();
            var inputs = Descendants<TextBox>(table).Where(input => input.IsVisible).ToList();
            var input = Assert.Single(inputs);
            var calls = 0;
            table.CellActionInvoked += (_, e) => { calls++; Assert.Equal("updated", e.Row["value"]); e.Completion = Task.FromResult(true); };
            Focus(input);
            input.Text = "updated";
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.True(UiTestHost.PumpUntil(() => calls == 1));
            host.Close();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Fact]
    public void EditorSubmitsCapturedIdentityAndRejectsDuplicateAndReadonlyEdits()
    {
        UiTestHost.RunSta(() =>
        {
            var column = new AuroraTableColumn("value", "值", CellEditAction: new("demo.save"), EditableWhen: "editable");
            var row = new Dictionary<string, string> { ["project"] = "A", ["value"] = "old", ["editable"] = "true" };
            var table = new AuroraTable();
            var editor = new AuroraTableCellEditor { Tag = column, DataContext = row, Submit = table.CommitCellEditAsync };
            editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var input = ((Grid)editor.Content).Children.OfType<TextBox>().Single();
            var pending = new TaskCompletionSource<bool>();
            var calls = new List<AuroraCellActionEventArgs>();
            table.CellActionInvoked += (_, args) => { calls.Add(args); args.Completion = pending.Task; };
            Focus(input);
            row["project"] = "B";
            input.Text = "new";
            Assert.Empty(calls);
            var saving = editor.SaveAsync();
            Assert.Single(calls);
            Assert.Equal("A", calls[0].Row["project"]);
            Assert.Equal("new", calls[0].Row["value"]);
            Assert.Equal("old", row["value"]);
            Assert.True(input.IsReadOnly);
            editor.SaveAsync().GetAwaiter().GetResult();
            Assert.Single(calls);
            // 请求尚未结束时虚拟化复用为另一行；旧响应不能回写新行。
            editor.DataContext = new Dictionary<string, string> { ["project"] = "C", ["value"] = "current", ["editable"] = "true" };
            pending.SetResult(false);
            Assert.True(UiTestHost.PumpUntil(() => saving.IsCompleted));
            Assert.Equal("current", input.Text);
            Assert.False(input.IsReadOnly);
            var refused = table.CommitCellEditAsync(column, new Dictionary<string, string> { ["editable"] = "false" }, "x");
            Assert.False(refused.GetAwaiter().GetResult());
            Assert.Single(calls);
        });
    }

    [Fact]
    public void CancelRefreshAndFailureNeverPersistDrafts()
    {
        UiTestHost.RunSta(() =>
        {
            var count = 0;
            var editor = new AuroraTableCellEditor
            {
                Tag = new AuroraTableColumn("value", "值", CellEditAction: new("demo.save")),
                DataContext = new Dictionary<string, string> { ["value"] = "old" },
                Submit = (_, _, _) => { count++; return Task.FromResult(false); },
            };
            editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var input = ((Grid)editor.Content).Children.OfType<TextBox>().Single();
            Focus(input);
            input.Text = "draft";
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.Equal("old", input.Text);
            Assert.Equal(0, count);
            input.Text = "rejected";
            editor.SaveAsync().GetAwaiter().GetResult();
            Assert.Equal("old", input.Text);
            Assert.Equal(1, count);
            editor.DataContext = new Dictionary<string, string> { ["value"] = "refreshed" };
            Assert.Equal("refreshed", input.Text);
            Assert.Equal(1, count);
        });
    }

    private static void Focus(TextBox input)
        => input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, input)
            { RoutedEvent = Keyboard.GotKeyboardFocusEvent });

    private sealed class FakeSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
