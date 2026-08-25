using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Table;
using HistoryAurora.Shell.Views;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 命令集与指令详情两页真的**建得起来、填得上数**（REQ-UI-014）。
///
/// 这两页在宿主 5.0 之后整体消失过一轮，而"消失"当时没有任何门禁能发现——
/// 它表现为主文档区空着，不是一条报错。这里两条用例分别钉住"页面在"与"数据在"。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class CommandPagesContractTests
{
    [Fact]
    public void Catalog_ListsTheRegistryWithoutAnyModulePresent()
    {
        UiTestHost.RunSta(() =>
        {
            var (session, bus, selection) = Host();
            var view = new CommandCatalogView(session, bus, new NullShellLog(), selection);
            var host = new Window { Content = view, Width = 900, Height = 500, ShowInTaskbar = false };

            try
            {
                host.Show();
                UiTestHost.PumpUntil(() => Table(view).RowCount > 0);

                var table = Table(view);
                Assert.Equal(2, table.RowCount);
                // 指令 / 类 / 只读 / 说明 —— 加上行操作那一列。
                Assert.Equal(4, table.ColumnCount);
                Assert.NotEmpty(table.RowActions);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Detail_FollowsTheSelectionAndShowsTheParameterTable()
    {
        UiTestHost.RunSta(() =>
        {
            var (session, bus, selection) = Host();
            var view = new CommandDetailView(session, bus, selection);
            var host = new Window { Content = view, Width = 400, Height = 400, ShowInTaskbar = false };

            try
            {
                host.Show();
                UiTestHost.Pump();

                selection.CurrentCommandName = "demo.branch.rename";
                UiTestHost.PumpUntil(() => Table(view).RowCount > 0);

                // 参数表就是"这条指令收什么"的全部答案，控制台以外只有这里能看到。
                var row = Assert.Single(Table(view).Data.Rows);
                Assert.Equal("to", row["c0"]);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static AuroraTable Table(DependencyObject root)
        => Assert.Single(Descendants<AuroraTable>(root));

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static (LocalCommandCatalogSession Session, CommandBus Bus, CommandSelectionState Selection) Host()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "demo.branch.rename",
            Domain = "demo",
            CommandClass = "branch",
            Summary = "重命名分支",
            Readonly = true,
            Parameters = [new ParameterSpec { Name = "to", Description = "新名字", Required = true }],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "demo.repo.list",
            Domain = "demo",
            CommandClass = "repo",
            Summary = "列出仓库",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });

        var log = new NullShellLog();
        var bus = new CommandBus(registry, log);
        return (new LocalCommandCatalogSession(bus, log), bus, new CommandSelectionState());
    }

    private sealed class NullShellLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message)
            => EntryAdded?.Invoke(this, new ShellLogEntry(DateTime.Now, level, category, message));

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
