
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell.Composition;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class DockingContractTests
{
    [Fact]
    public void ToolWindowDefaultsToRightAndModulesCanOverridePlacement()
    {
        var defaultWindow = new ToolWindowDescriptor
        {
            Id = "module.default",
            Title = "module.default",
        };
        var overriddenWindow = new ToolWindowDescriptor
        {
            Id = "module.override",
            Title = "module.override",
            DefaultSide = DockSide.Top,
        };

        Assert.Equal(DockSide.Right, defaultWindow.DefaultSide);
        Assert.Equal(0.25, defaultWindow.DefaultRatio);
        Assert.Equal(DockSide.Top, overriddenWindow.DefaultSide);
    }

    /// <summary>
    /// REQ-UI-083：AvalonDock 自己建出来的中央文档窗格必须被换掉。
    ///
    /// 把一页浮出去再丢回中央区、或者把中央区拖成两半，新窗格是原装的
    /// <c>LayoutDocumentPane</c>，它的 <c>IndexOf</c> 不认工具页——「换不了页、拖不动」
    /// 会在那一格里当场复发，而且只在拖过的那台机器上复发。
    /// </summary>
    [Fact]
    public void StockDocumentPaneCreatedByAvalonDockIsUpgraded()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();
            host.Show(StandardWindowIds.Mcp);

            // 模拟 AvalonDock 的手笔：把中央区整个换成一个原装窗格。
            // 顶栏只留一页（REQ-UI-096），此刻 business 藏着，它记着的「上一个容器」就是即将被换掉的这一格。
            var original = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            var stock = new LayoutDocumentPane();
            foreach (var child in original.Children.ToList())
            {
                original.Children.Remove(child);
                stock.Children.Add(child);
            }

            ((ILayoutContainer)original.Parent!).ReplaceChild(original, stock);
            manager.Layout.CollectGarbage();

            // 任何一次经过布局差分的操作都应该把它换掉；藏着的 business 回来时
            // 那个旧窗格已经不在树里，得落进新的主文档区，而不是跟着旧窗格一起消失。
            host.Show("business");

            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            Assert.IsType<CenterDocumentPane>(pane);
            Assert.Equal(["business"], pane.Children.Select(item => item.ContentId));
            Assert.Equal("business", pane.SelectedContent?.ContentId);
            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);
        });
    }

    /// <summary>
    /// 注册协议给不出「主页面」：文档身份只属于 Aurora 自己的命令集，
    /// 任何模块声明 <c>side=center</c> 拿到的都是工具窗口——位置在中央区，身份是工具页。
    ///
    /// 这条不是本轮新加的行为，而是把一条**一直成立、却没人守着**的约束钉进门禁：
    /// 谁要是哪天给 <c>UsesDocumentIdentity</c> 开第二个口子，这里会红。
    /// </summary>
    [Fact]
    public void RegistrationNeverProducesASecondMainDocument()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();
            host.RegisterWindow(Tool("late", DockSide.Center, 1), "module:test");

            // 整棵布局里只有命令集是文档；其余中央页都是工具窗口。
            Assert.Equal(
                [StandardWindowIds.Mcp],
                manager.Layout.Descendents().OfType<LayoutDocument>().Select(item => item.ContentId));
            foreach (var id in new[] { "business", "late" })
            {
                var anchorable = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == id);
                Assert.True(anchorable.CanDockAsTabbedDocument);

                // 顶栏只留一页（REQ-UI-096）：它此刻可能被顶掉了，显示出来位置仍是中央区。
                host.Show(id);
                Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == id).Side);
            }
        });
    }

    /// <summary>
    /// REQ-UI-083：<c>aurora.ui.show</c> 对中央区里的**工具页**必须真的换页。
    ///
    /// 1.18.2 之前它报成功但什么都不换——`LayoutDocumentPane` 的 `IndexOf` 对 anchorable
    /// 返回 -1，`IsSelected` 于是把窗格写成「什么都不选」。
    /// </summary>
    [Fact]
    public void ShowSelectsACenterToolPage()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                    Tool("other", DockSide.Center, 1),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();

            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            var business = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "business");

            host.Show(StandardWindowIds.Mcp);
            Assert.Equal(StandardWindowIds.Mcp, pane.SelectedContent?.ContentId);

            host.Show("business");
            Assert.Equal("business", pane.SelectedContent?.ContentId);
            Assert.Same(business, pane.SelectedContent);
            Assert.Equal(pane.Children.IndexOf(business), pane.SelectedContentIndex);

            host.Show("other");
            Assert.Equal("other", pane.SelectedContent?.ContentId);
        });
    }

    [Fact]
    public void CenterIsExplicitAndUsesTheMainDocumentPane()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [Tool("stage", DockSide.Center, 1)],
                new MemoryLayoutStore(),
                new NullLog());

            host.Initialize();

            var state = Assert.Single(host.ListWindows());
            Assert.Equal(DockSide.Center, state.Side);
            Assert.Null(state.Ratio);
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            var anchorable = Assert.Single(pane.Children.OfType<LayoutAnchorable>());
            Assert.Equal("stage", anchorable.ContentId);
            Assert.True(anchorable.CanDockAsTabbedDocument);
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutDocument>(),
                item => item.ContentId == "stage");
            Assert.Equal(GridUnitType.Star, pane.DockWidth.GridUnitType);
            var dock = FrontendCommandCatalog.FrameworkSourceDescriptors.Single(item => item.Name == "aurora.ui.dock");
            Assert.Contains("center", dock.Parameters.Single(item => item.Name == "pos").AllowedValues!);
        });
    }

    /// <summary>
    /// REQ-UI-095：命令集不再是主文档区的锚点。1.19.0 及以前它藏不掉、浮不出（窗口按钮栏挂在它那格上），
    /// 现在可以隐藏、可以浮出，藏着也不会被中央区修复放回来；它仍是文档身份，停靠只认中央区。
    /// </summary>
    [Fact]
    public void CommandCatalogIsNoLongerTheCenterAnchor()
    {
        UiTestHost.RunSta(() =>
        {
            var window = ShowHost(
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1), Tool("console", DockSide.Bottom, 0.28)],
                new MemoryLayoutStore(),
                out var host);
            try
            {
                host.Hide(StandardWindowIds.Mcp);
                // 越过布局差分的去抖：中央区修复要是还把它当锚点，会在这段时间里把它放回来。
                UiTestHost.PumpFor(700);
                Assert.False(InfoOf(host, StandardWindowIds.Mcp).IsVisible);

                host.Show(StandardWindowIds.Mcp);
                Assert.Equal(DockSide.Center, InfoOf(host, StandardWindowIds.Mcp).Side);

                host.Float(StandardWindowIds.Mcp);
                UiTestHost.Pump();
                Assert.True(InfoOf(host, StandardWindowIds.Mcp).IsFloating);

                host.Dock(StandardWindowIds.Mcp, DockSide.Right, 0.25);
                var docked = InfoOf(host, StandardWindowIds.Mcp);
                Assert.True(docked.IsVisible);
                Assert.False(docked.IsFloating);
                Assert.Equal(DockSide.Center, docked.Side);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// REQ-UI-096：顶栏只留一页。显示、停靠进中央区的那一页留下，原来那一页隐藏；
    /// 最后一页没了，主文档区空着——不再把命令集拽回来。
    /// </summary>
    [Fact]
    public void CenterHoldsOnlyTheLastPageShown()
    {
        UiTestHost.RunSta(() =>
        {
            var host = new DockingHost(
                new DockingManager(),
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                    Tool("other", DockSide.Right, 0.3),
                ],
                new MemoryLayoutStore(),
                new NullLog());

            host.Initialize();
            Assert.Single(VisibleCenterIds(host));

            host.Show("business");
            Assert.Equal(["business"], VisibleCenterIds(host));
            host.Show(StandardWindowIds.Mcp);
            Assert.Equal([StandardWindowIds.Mcp], VisibleCenterIds(host));
            Assert.False(InfoOf(host, "business").IsVisible);

            host.Dock("other", DockSide.Center);
            Assert.Equal(["other"], VisibleCenterIds(host));

            host.UnregisterWindow("other");
            Assert.Empty(VisibleCenterIds(host));

            host.Show("business");
            Assert.Equal(["business"], VisibleCenterIds(host));
        });
    }

    /// <summary>
    /// REQ-UI-098：拖出去没落到停靠点的页被收起来——先摆回默认位置再隐藏，下次打开回到停靠位，
    /// 而不是那个已经关掉的浮窗；收起的那一下不许顶掉顶栏原来那一页。
    /// </summary>
    [Fact]
    public void ParkedPageComesBackDockedAndLeavesTheCenterAlone()
    {
        UiTestHost.RunSta(() =>
        {
            var window = ShowHost(
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("page", DockSide.Center, 1),
                    Tool("tool", DockSide.Right, 0.3),
                ],
                new MemoryLayoutStore(),
                out var host);
            try
            {
                host.Show(StandardWindowIds.Mcp);
                host.Float("tool");
                host.Float("page");
                UiTestHost.Pump();
                Assert.True(InfoOf(host, "tool").IsFloating);
                Assert.True(InfoOf(host, "page").IsFloating);

                host.ParkHidden("tool");
                host.ParkHidden("page");
                UiTestHost.Pump();
                Assert.False(InfoOf(host, "tool").IsVisible);
                Assert.False(InfoOf(host, "page").IsVisible);
                Assert.Equal([StandardWindowIds.Mcp], VisibleCenterIds(host));

                host.Show("tool");
                var tool = InfoOf(host, "tool");
                Assert.True(tool.IsVisible);
                Assert.False(tool.IsFloating);
                Assert.Equal(DockSide.Right, tool.Side);

                host.Show("page");
                Assert.Equal(["page"], VisibleCenterIds(host));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// REQ-UI-096：新登记的中央页不抢顶栏，哪怕位置台账说它上次是选中的那一页。
    /// 台账是全局的、不分场景——1.20.0 首次热装时 1.19.0 台账里「上次选中」的模块页把命令集顶掉，
    /// 随后场景又把这个外来页藏起来，顶栏一页不剩。主文档区空着时，新来的页才留下。
    /// </summary>
    [Fact]
    public void RegisteredCenterPageNeverTakesTheTopBarFromAPageAlreadyThere()
    {
        UiTestHost.RunSta(() =>
        {
            var settings = new MemorySettings();
            settings.Set(
                "layout.placements",
                "{\"module.page\":{\"Side\":5,\"Ratio\":0.25,\"Hidden\":false,\"TabTarget\":null," +
                "\"CenterIndex\":0,\"Selected\":true}}");
            var host = new DockingHost(
                new DockingManager(),
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                new MemoryLayoutStore(),
                new NullLog(),
                settings);
            host.Initialize();
            Assert.Equal([StandardWindowIds.Mcp], VisibleCenterIds(host));

            host.RegisterWindow(Tool("module.page", DockSide.Center, 1), "module:test");
            Assert.Equal([StandardWindowIds.Mcp], VisibleCenterIds(host));

            host.Hide(StandardWindowIds.Mcp);
            host.RegisterWindow(Tool("module.late", DockSide.Center, 1), "module:test");
            Assert.Equal(["module.late"], VisibleCenterIds(host));
        });
    }

    private static ToolWindowInfo InfoOf(DockingHost host, string id)
        => host.ListWindows().Single(window => window.Id == id);

    private static string[] VisibleCenterIds(DockingHost host)
        => host.ListWindows()
            .Where(window => window is { IsVisible: true, IsFloating: false, Side: DockSide.Center })
            .Select(window => window.Id)
            .ToArray();

    [Fact]
    public void RestoreSkipsRemovedPageAndAddsTheCurrentCommandCatalog()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var legacyWindow = ShowHost(
                [
                    Tool("resource", DockSide.Left, 0.18),
                    Tool("legacy-command-catalog", DockSide.Right, 0.3),
                    Tool("details", DockSide.Right, 0.3),
                    Tool("console", DockSide.Bottom, 0.28),
                ], store, out var legacyHost);
            try
            {
                UiTestHost.Pump();
                legacyHost.SaveCurrentLayout();
                store.ReplaceCurrent("legacy-command-catalog", "removed-command-catalog");
            }
            finally
            {
                legacyWindow.Close();
            }

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool("resource", DockSide.Left, 0.18),
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.3),
                    Tool("console", DockSide.Bottom, 0.28),
                ],
                store,
                new NullLog());
            host.Initialize();

            var windows = host.ListWindows().ToDictionary(window => window.Id);
            Assert.Equal(DockSide.Left, windows["resource"].Side);
            Assert.Equal(DockSide.Center, windows[StandardWindowIds.Mcp].Side);
            Assert.Equal(DockSide.Right, windows["details"].Side);
            Assert.Equal(DockSide.Bottom, windows["console"].Side);
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            Assert.Equal(
                StandardWindowIds.Mcp,
                Assert.Single(pane.Children.OfType<LayoutDocument>()).ContentId);
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutAnchorable>(),
                item => item.ContentId == StandardWindowIds.Mcp);
        });
    }

    // 3.11.3+: module center entries are tool windows hosted in the main document pane.
    [Fact]
    public void ShellWindowHostsSelectablePagesInTheFullMainDocumentPane()
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-center-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var window = new ShellWindow(
                new ShellConfig
                {
                    AppName = "HistoryVulcan Center Test",
                    AppVersion = "3.0.1",
                },
                new MemoryLayoutStore(),
                new NullLog(),
                new MemorySettings(),
                dataDirectory)
            {
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            // 1.7.0 起命令集由 Aurora 自建并在构造期注册（REQ-UI-014），因此这里不再补一个等价页——
            // 补了会撞 id。这条断言的仍然是主文档区的几何与页签行为，只是主文档现在真的有内容。
            window.Docking.Show(StandardWindowIds.Mcp);

            try
            {
                Assert.Null(window.Modules);
                // 界面自己不注册指令目录：vulcan.command.* 归宿主（Vulcan 4.4.0）。
                Assert.False(window.Commands.Registry.TryGet("vulcan.command.list", out _));
                // 两个名字都不许出现：改名前的与改名后的。
                Assert.False(window.Commands.Registry.TryGet("vulcan.mcp.start", out _));
                Assert.False(window.Commands.Registry.TryGet("portunus.mcp.start", out _));
                Assert.False(window.Commands.Registry.TryGet("vulcan.module.list", out _));
                window.Show();
                UiTestHost.Pump();
                var single = Assert.Single(FindVisualDescendants<LayoutDocumentPaneControl>(window));
                // 1.20.0 起顶栏只留一页（REQ-UI-096）：组件测试页与命令集都声明 side=center，
                // 中央区里却只有一页。本条断言的始终是主文档区的几何与页签行为。
                Assert.Single(single.Items);
                Assert.True(single.ActualWidth > window.ActualWidth * 0.5,
                    $"main document width={single.ActualWidth}, window width={window.ActualWidth}");
                Assert.Equal(
                    Visibility.Visible,
                    Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(single)).Visibility);

                window.Docking.RegisterWindow(Tool("business", DockSide.Center, 1), "test");
                window.Docking.Show("business");
                UiTestHost.Pump();
                var multiple = Assert.Single(FindVisualDescendants<LayoutDocumentPaneControl>(window));
                // 刚显示的 business 顶掉了原来那一页：中央区仍然只有一页，就是它。
                Assert.Single(multiple.Items);
                var centerPane = Assert.IsAssignableFrom<LayoutDocumentPane>(((ILayoutControl)multiple).Model);
                var business = Assert.Single(
                    centerPane.Children.OfType<LayoutAnchorable>(),
                    item => item.ContentId == "business");
                Assert.True(business.IsSelected || business.IsActive);
                Assert.True(multiple.ActualWidth > window.ActualWidth * 0.5,
                    $"main document width={multiple.ActualWidth}, window width={window.ActualWidth}");
                Assert.Equal(
                    Visibility.Visible,
                    Assert.Single(FindVisualDescendants<DocumentPaneTabPanel>(multiple)).Visibility);
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void SideToolCanBeDraggedIntoTheMainDocumentPaneAndBackOut()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.25),
                ],
                store,
                new NullLog());
            host.Initialize();

            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            var details = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "details");
            Assert.True(details.CanDockAsTabbedDocument);

            ((ILayoutContainer)details.Parent!).RemoveChild(details);
            pane.Children.Add(details);
            manager.Layout.CollectGarbage();
            UiTestHost.Pump();

            Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == "details").Side);
            Assert.Same(pane, details.Parent);

            host.Dock("details", DockSide.Right, 0.3);
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "details").Side);

            host.Dock("details", DockSide.Center);
            Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == "details").Side);
            Assert.Same(pane, manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "details").Parent);
            host.SaveCurrentLayout();

            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.25),
                ],
                store,
                new NullLog());
            recoveredHost.Initialize();

            Assert.Equal(DockSide.Center, recoveredHost.ListWindows().Single(item => item.Id == "details").Side);
            var recoveredDetails = recoveredManager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "details");
            Assert.True(recoveredDetails.CanDockAsTabbedDocument);
            Assert.IsAssignableFrom<LayoutDocumentPane>(recoveredDetails.Parent);
        });
    }

    [Fact]
    public void LateRegisteredSideToolRestoresIntoCenterWithoutLosingToolIdentity()
    {
        UiTestHost.RunSta(() =>
        {
            var settings = new MemorySettings();
            settings.Set(
                "layout.placements",
                "{\"module.first\":{\"Side\":5,\"Ratio\":0.25,\"Hidden\":false,\"TabTarget\":null," +
                "\"CenterIndex\":2,\"Selected\":false}," +
                "\"module.second\":{\"Side\":5,\"Ratio\":0.25,\"Hidden\":false,\"TabTarget\":null," +
                "\"CenterIndex\":1,\"Selected\":false}}");
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                new MemoryLayoutStore(),
                new NullLog(),
                settings);
            host.Initialize();

            host.RegisterWindow(Tool("module.first", DockSide.Right, 0.25), "module:test");
            host.RegisterWindow(Tool("module.second", DockSide.Right, 0.25), "module:test");

            // 顶栏只留一页（REQ-UI-096）：两页都按台账落回中央区，但同一时刻只露一页。
            // 契约是「落回中央区、身份仍是工具页」，逐页显示出来验。
            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
            foreach (var id in new[] { "module.first", "module.second" })
            {
                host.Show(id);
                Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == id).Side);
                var shown = Assert.Single(pane.Children);
                Assert.Equal(id, shown.ContentId);
                Assert.True(Assert.IsType<LayoutAnchorable>(shown).CanDockAsTabbedDocument);
            }

            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutDocument>(),
                item => item.ContentId?.StartsWith("module.", StringComparison.Ordinal) == true);

            host.Dock("module.first", DockSide.Right, 0.3);
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "module.first").Side);
        });
    }

    [Fact]
    public void LateRegisteredDefaultModuleWindowJoinsExistingRightPane()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool(StandardWindowIds.CommandDetail, DockSide.Right, 0.32),
                    Tool(StandardWindowIds.Modules, DockSide.Right, 0.32),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();

            host.RegisterWindow(new ToolWindowDescriptor
            {
                Id = "module.registered",
                Title = "module.registered",
                ContentFactory = () => new Border(),
            }, "module:test");

            var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutAnchorablePane>());
            Assert.Equal(
                [StandardWindowIds.CommandDetail, StandardWindowIds.Modules, "module.registered"],
                pane.Children.Select(item => item.ContentId));
            Assert.Equal("module.registered", pane.SelectedContent?.ContentId);
            var registered = host.ListWindows().Single(item => item.Id == "module.registered");
            Assert.Equal(DockSide.Right, registered.Side);
            Assert.Equal("module:test", registered.Owner);

            host.ResetWindow("module.registered");
            Assert.Same(
                pane,
                manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "module.registered").Parent);
        });
    }

    [Fact]
    public void NamedLayoutPreservesHiddenBusinessCenterPage()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();
            host.Hide("business");
            host.SaveLayout("hidden-center");

            host.Show("business");
            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);

            Assert.True(host.LoadLayout("hidden-center"));
            Assert.False(host.ListWindows().Single(item => item.Id == "business").IsVisible);
            Assert.DoesNotContain(
                manager.Layout.Descendents().OfType<LayoutContent>(),
                item => item.ContentId == "business" && item.Parent is LayoutDocumentPane);
        });
    }

    [Fact]
    public void FloatingCenterPageRoundTripsWithoutInvalidatingMainLayout()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("business", DockSide.Center, 1),
            };
            var window = ShowHost(descriptors, store, out var host);
            try
            {
                // 命令集留在顶栏，business 此刻被顶掉藏着：从藏着的状态直接浮出，也得报「浮着」。
                host.Show(StandardWindowIds.Mcp);
                host.Float("business");
                UiTestHost.Pump();
                Assert.True(host.ListWindows().Single(item => item.Id == "business").IsFloating);
                host.SaveCurrentLayout();
            }
            finally
            {
                window.Close();
            }

            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager,
                descriptors,
                store,
                new NullLog());
            recoveredHost.Initialize();

            var recovered = recoveredHost.ListWindows().Single(item => item.Id == "business");
            Assert.True(recovered.IsVisible);
            Assert.True(recovered.IsFloating);
            var mainPane = Assert.Single(
                recoveredManager.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>());
            Assert.Contains(
                mainPane.Children.OfType<LayoutDocument>(),
                item => item.ContentId == StandardWindowIds.Mcp);
        });
    }

    /// <summary>
    /// 回归(2026-09-06 真机):把中央页拖出去再丢回中央区,AvalonDock 会把中央区拆成
    /// <c>LayoutDocumentPaneGroup</c> 下的两个 <c>LayoutDocumentPane</c>。
    /// 此前 <c>DetectSide</c> 与 <c>EnsureCenterColumn</c> 用 <c>SingleOrDefault</c> 取中央区,
    /// 于是布局差分的定时器每一拍都抛 <c>Sequence contains more than one element</c>
    /// ——真机 11 秒里 158 条未处理异常,基线再也建不起来,随后的 <c>aurora.ui.float</c>
    /// 一并失败,存盘则抛「必须且只能存在一个中央主文档区」。
    /// 三条断言分别压住三个改点:差分、停靠、存盘。
    /// </summary>
    [Fact]
    public void SplitCenterKeepsDiffsDockingAndSavingAlive()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            // 顶栏只留一页之后，中央区被拆成两半的来路是「把侧边页丢到中央区边上」。
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("business", DockSide.Right, 0.25),
                Tool("side", DockSide.Left, 0.2),
            };
            var window = ShowHost(descriptors, store, out var host);
            try
            {
                var manager = (DockingManager)window.Content;
                SplitCenterArea(manager, "business");
                UiTestHost.Pump();
                Assert.Equal(
                    2,
                    manager.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>().Count());

                // DetectSide:侧边窗口仍要认出自己在左边,而不是把整轮差分连同基线一起抛掉。
                Assert.Equal(DockSide.Left, host.ListWindows().Single(item => item.Id == "side").Side);

                // EnsureCenterColumn:换边要照常落地。
                host.Dock("side", DockSide.Bottom, 0.3);
                UiTestHost.Pump();
                Assert.Equal(DockSide.Bottom, host.ListWindows().Single(item => item.Id == "side").Side);

                // LayoutHasMainDocumentPane:分栏后的中央区照样要能存盘。
                host.SaveCurrentLayout();
                Assert.NotNull(store.ReadCurrent());
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 回归(2026-09-06 真机):拖拽中途失败会留下一个「有页签、没内容」的文档区。
    /// AvalonDock 新建的文档区把 <c>SelectedContentIndex</c> 停在 -1,而窗格模板里的
    /// <c>PART_SelectedContentHost</c> 绑的是 <c>SelectedContent</c>——页签照画,内容整片空白,
    /// 且 <c>aurora.ui.show</c> 救不回来(它只改 <c>IsSelected</c>,不改窗格的选中下标)。
    /// </summary>
    [Fact]
    public void DocumentPaneWithChildrenAlwaysKeepsASelectedContent()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var window = ShowHost(
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Right, 0.25),
                ],
                store,
                out var host);
            try
            {
                var manager = (DockingManager)window.Content;
                SplitCenterArea(manager, "business");
                UiTestHost.Pump();

                var panes = manager.Layout.RootPanel.Descendents()
                    .OfType<LayoutDocumentPane>()
                    .ToList();
                Assert.Equal(2, panes.Count);
                Assert.All(panes, pane =>
                {
                    Assert.NotEmpty(pane.Children);
                    Assert.InRange(pane.SelectedContentIndex, 0, pane.Children.Count - 1);
                    Assert.NotNull(pane.SelectedContent);
                });
                Assert.Equal(2, host.ListWindows().Count(item => item.Side == DockSide.Center));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void JsonSnapshotPreservesNestedSplitsTabsSelectionAndMultipleFloatingWindows()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("left.top", DockSide.Left, 0.2),
                Tool("left.bottom", DockSide.Left, 0.2),
                Tool("right.one", DockSide.Right, 0.25),
                Tool("right.two", DockSide.Right, 0.25),
                Tool("float.one", DockSide.Right, 0.25),
                Tool("float.two", DockSide.Right, 0.25),
            };
            double savedFloatTwoWidth = 0;
            double savedFloatTwoHeight = 0;
            double savedLeftTopHeight = 0;
            GridUnitType savedLeftTopHeightUnit = GridUnitType.Auto;
            var first = ShowHost(descriptors, store, out var firstHost);
            try
            {
                var manager = (DockingManager)first.Content;
                var command = manager.Layout.Descendents().OfType<LayoutDocument>()
                    .Single(item => item.ContentId == StandardWindowIds.Mcp);
                var anchorables = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .ToDictionary(item => item.ContentId!);
                foreach (var item in anchorables.Values.Cast<LayoutContent>().Append(command))
                    ((ILayoutContainer)item.Parent!).RemoveChild(item);

                var leftTop = new LayoutAnchorablePane(anchorables["left.top"])
                {
                    DockHeight = new GridLength(0.35, GridUnitType.Star),
                };
                var leftBottom = new LayoutAnchorablePane(anchorables["left.bottom"])
                {
                    DockHeight = new GridLength(0.65, GridUnitType.Star),
                };
                var leftColumn = new LayoutPanel(leftTop)
                {
                    Orientation = Orientation.Vertical,
                    DockWidth = new GridLength(0.22, GridUnitType.Star),
                };
                leftColumn.Children.Add(leftBottom);

                var centerPane = new LayoutDocumentPane(command);
                var centerColumn = new LayoutPanel(centerPane)
                {
                    Orientation = Orientation.Vertical,
                    DockWidth = new GridLength(0.53, GridUnitType.Star),
                };
                var rightPane = new LayoutAnchorablePane(anchorables["right.one"])
                {
                    DockWidth = new GridLength(0.25, GridUnitType.Star),
                };
                rightPane.Children.Add(anchorables["right.two"]);
                rightPane.SelectedContentIndex = 1;

                var rootPanel = new LayoutPanel(leftColumn) { Orientation = Orientation.Horizontal };
                rootPanel.Children.Add(centerColumn);
                rootPanel.Children.Add(rightPane);
                var root = new LayoutRoot { RootPanel = rootPanel, ActiveContent = anchorables["right.two"] };
                AddFloating(root, anchorables["float.one"], 1_000_000, 1_000_000, 720, 510);
                AddFloating(root, anchorables["float.two"], 120, 90, 540, 360);
                manager.Layout = root;
                manager.UpdateLayout();
                UiTestHost.Pump();
                savedFloatTwoWidth = anchorables["float.two"].FloatingWidth;
                savedFloatTwoHeight = anchorables["float.two"].FloatingHeight;
                savedLeftTopHeight = leftTop.DockHeight.Value;
                savedLeftTopHeightUnit = leftTop.DockHeight.GridUnitType;

                firstHost.SaveCurrentLayout();
                var payload = Assert.IsType<string>(store.ReadCurrent());
                Assert.StartsWith("{", payload, StringComparison.Ordinal);
                Assert.Contains("\"schemaVersion\": 1", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("AvalonDock", payload, StringComparison.Ordinal);
                Assert.DoesNotContain("$type", payload, StringComparison.Ordinal);
            }
            finally
            {
                first.Close();
            }

            var restored = ShowHost(descriptors, store, out _);
            try
            {
                UiTestHost.Pump();
                var manager = (DockingManager)restored.Content;
                var leftTop = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "left.top");
                var leftBottom = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "left.bottom");
                Assert.NotSame(leftTop.Parent, leftBottom.Parent);
                Assert.Same(leftTop.Parent!.Parent, leftBottom.Parent!.Parent);
                Assert.Equal(
                    Orientation.Vertical,
                    Assert.IsType<LayoutPanel>(leftTop.Parent.Parent).Orientation);
                var restoredLeftTopPane = Assert.IsType<LayoutAnchorablePane>(leftTop.Parent);
                Assert.Equal(savedLeftTopHeightUnit, restoredLeftTopPane.DockHeight.GridUnitType);
                Assert.InRange(Math.Abs(restoredLeftTopPane.DockHeight.Value - savedLeftTopHeight), 0, 0.01);

                var right = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Where(item => item.ContentId is "right.one" or "right.two")
                    .ToArray();
                Assert.Equal(["right.one", "right.two"], right.Select(item => item.ContentId));
                var rightPane = Assert.IsType<LayoutAnchorablePane>(right[0].Parent);
                Assert.Same(rightPane, right[1].Parent);
                Assert.Equal("right.two", rightPane.SelectedContent?.ContentId);

                Assert.Equal(2, manager.Layout.FloatingWindows.Count);
                var floating = manager.Layout.FloatingWindows
                    .SelectMany(window => window.Descendents().OfType<LayoutAnchorable>())
                    .ToDictionary(item => item.ContentId!);
                Assert.Equal(2, floating.Count);
                Assert.InRange(
                    floating["float.one"].FloatingLeft,
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth);
                Assert.InRange(
                    Math.Abs(floating["float.two"].FloatingWidth - savedFloatTwoWidth), 0, 1);
                Assert.InRange(
                    Math.Abs(floating["float.two"].FloatingHeight - savedFloatTwoHeight), 0, 1);
            }
            finally
            {
                restored.Close();
            }
        });
    }

    [Fact]
    public void SnapshotPlacementWaitsForALateRegisteredPage()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var savedDescriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("right.leader", DockSide.Right, 0.3),
                Tool("module.late", DockSide.Right, 0.3),
            };
            var first = new DockingHost(
                new DockingManager(), savedDescriptors, store, new NullLog());
            first.Initialize();
            first.Dock("module.late", DockSide.Tab, targetId: "right.leader");
            first.SaveCurrentLayout();

            var manager = new DockingManager();
            var restored = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("right.leader", DockSide.Right, 0.3),
                ],
                store,
                new NullLog());
            restored.Initialize();
            restored.RegisterWindow(Tool("module.late", DockSide.Left, 0.1), "module:test");

            var leader = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "right.leader");
            var late = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "module.late");
            Assert.Same(leader.Parent, late.Parent);
            Assert.Equal(DockSide.Right, restored.ListWindows().Single(item => item.Id == "module.late").Side);
        });
    }

    [Fact]
    public void MissingPageIsCollapsedAndCanClaimItsSavedPlacementLater()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var source = new DockingHost(
                new DockingManager(),
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("module.late", DockSide.Left, 0.2),
                ],
                store,
                new NullLog());
            source.Initialize();
            source.SaveCurrentLayout();

            var manager = new DockingManager();
            var restored = new DockingHost(
                manager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            restored.Initialize();

            Assert.Empty(manager.Layout.RootPanel.Descendents().OfType<LayoutAnchorablePane>());
            restored.RegisterWindow(Tool("module.late", DockSide.Right, 0.4), "module:test");
            Assert.Equal(
                DockSide.Left,
                restored.ListWindows().Single(item => item.Id == "module.late").Side);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not-json")]
    // 1.7.0 之前的 XML 存档。壳里早已没有 XML 反序列化路径,它对读取侧就是又一份非 JSON 负载,
    // 走的是同一条回退:落到位置台账,且不删掉原文件。
    [InlineData("<LayoutRoot />")]
    public void MissingOrCorruptSnapshotFallsBackToPlacementsWithoutDeletingIt(string? payload)
    {
        UiTestHost.RunSta(() =>
        {
            var settings = new MemorySettings();
            settings.Set(
                "layout.placements",
                "{\"right.leader\":{\"Side\":1,\"Ratio\":0.31,\"Hidden\":false,\"TabTarget\":null}," +
                "\"right.follower\":{\"Side\":4,\"Ratio\":0.31,\"Hidden\":false,\"TabTarget\":\"right.leader\"}," +
                "\"right.hidden\":{\"Side\":4,\"Ratio\":0.31,\"Hidden\":true,\"TabTarget\":\"right.leader\"}}");
            var store = new MemoryLayoutStore();
            if (payload != null)
                store.WriteCurrent(payload);
            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("right.leader", DockSide.Left, 0.2),
                    Tool("right.follower", DockSide.Left, 0.2),
                    Tool("right.hidden", DockSide.Left, 0.2),
                ],
                store,
                new NullLog(),
                settings);

            host.Initialize();

            var leader = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "right.leader");
            var follower = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "right.follower");
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "right.leader").Side);
            Assert.Same(leader.Parent, follower.Parent);
            Assert.False(host.ListWindows().Single(item => item.Id == "right.hidden").IsVisible);

            host.Show("right.hidden");
            var hidden = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "right.hidden");
            Assert.Same(leader.Parent, hidden.Parent);
            Assert.Equal(payload, store.ReadCurrent());
        });
    }

    [Fact]
    public void AutoHiddenGroupsRoundTripWithoutXmlSerialization()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("details", DockSide.Right, 0.3),
            };
            var first = ShowHost(descriptors, store, out var firstHost);
            try
            {
                firstHost.ToggleAutoHide("details");
                UiTestHost.Pump();
                var manager = (DockingManager)first.Content;
                Assert.True(manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "details").IsAutoHidden);
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var restored = ShowHost(descriptors, store, out _);
            try
            {
                UiTestHost.Pump();
                var manager = (DockingManager)restored.Content;
                Assert.True(manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "details").IsAutoHidden);
            }
            finally
            {
                restored.Close();
            }
        });
    }

    [Fact]
    public void HiddenPageKeepsItsLastTabGroupWithoutASettingsService()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("right.leader", DockSide.Right, 0.3),
                Tool("right.hidden", DockSide.Left, 0.1),
            };
            var first = new DockingHost(new DockingManager(), descriptors, store, new NullLog());
            first.Initialize();
            first.Dock("right.hidden", DockSide.Tab, targetId: "right.leader");
            first.Hide("right.hidden");
            first.SaveCurrentLayout();

            var manager = new DockingManager();
            var restored = new DockingHost(manager, descriptors, store, new NullLog());
            restored.Initialize();
            Assert.False(restored.ListWindows().Single(item => item.Id == "right.hidden").IsVisible);

            restored.Show("right.hidden");
            var leader = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "right.leader");
            var hidden = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(item => item.ContentId == "right.hidden");
            Assert.Same(leader.Parent, hidden.Parent);
        });
    }

    [Fact]
    public void SavingWhileMaximizedPersistsTheStablePreFocusLayout()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("details", DockSide.Right, 0.3),
            };
            var first = new DockingHost(new DockingManager(), descriptors, store, new NullLog());
            first.Initialize();
            first.MaximizeWindow(StandardWindowIds.Mcp);
            first.SaveCurrentLayout();

            var restored = new DockingHost(
                new DockingManager(), descriptors, store, new NullLog());
            restored.Initialize();
            var states = restored.ListWindows().ToDictionary(item => item.Id);
            Assert.Equal(DockSide.Center, states[StandardWindowIds.Mcp].Side);
            Assert.Equal(DockSide.Right, states["details"].Side);
            Assert.Null(restored.MaximizedId);
        });
    }

    [Fact]
    public void UnsupportedOrDuplicateSnapshotFallsBackToDefaultLayout()
    {
        UiTestHost.RunSta(() =>
        {
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("right.one", DockSide.Right, 0.25),
                Tool("right.two", DockSide.Right, 0.25),
            };

            var unsupported = new MemoryLayoutStore();
            var source = new DockingHost(new DockingManager(), descriptors, unsupported, new NullLog());
            source.Initialize();
            source.SaveCurrentLayout();
            unsupported.ReplaceCurrent("\"schemaVersion\": 1", "\"schemaVersion\": 99");
            var recovered = new DockingHost(
                new DockingManager(), descriptors, unsupported, new NullLog());
            recovered.Initialize();
            Assert.All(recovered.ListWindows(), item => Assert.True(item.IsVisible));
            Assert.Contains("\"schemaVersion\": 99", unsupported.ReadCurrent(), StringComparison.Ordinal);

            var duplicate = new MemoryLayoutStore();
            source = new DockingHost(new DockingManager(), descriptors, duplicate, new NullLog());
            source.Initialize();
            source.SaveCurrentLayout();
            duplicate.ReplaceCurrent("\"id\": \"right.two\"", "\"id\": \"right.one\"");
            var duplicateManager = new DockingManager();
            recovered = new DockingHost(duplicateManager, descriptors, duplicate, new NullLog());
            recovered.Initialize();
            var right = duplicateManager.Layout.Descendents().OfType<LayoutAnchorable>()
                .Where(item => item.ContentId is "right.one" or "right.two")
                .ToArray();
            Assert.Equal(2, right.Length);
            Assert.Same(right[0].Parent, right[1].Parent);
            Assert.NotNull(duplicate.ReadCurrent());
        });
    }

    [Fact]
    public void FileLayoutStoreUsesVersionedJsonAndKeepsTheOldFileWhenReplaceFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aurora-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FileLayoutStore(directory);
            store.WriteCurrent("old");
            var path = Path.Combine(directory, "layout.v1.json");
            Assert.True(File.Exists(path));

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsAny<IOException>(() => store.WriteCurrent("new"));

            Assert.Equal("old", File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DefaultTabIntoCenterKeepsToolIdentityAcrossResetAndRestore()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                new ToolWindowDescriptor
                {
                    Id = "details",
                    Title = "details",
                    DefaultSide = DockSide.Tab,
                    DefaultTabTarget = StandardWindowIds.Mcp,
                    DefaultRatio = 0.25,
                    ContentFactory = () => new Border(),
                },
            };
            var manager = new DockingManager();
            var host = new DockingHost(manager, descriptors, store, new NullLog());
            host.Initialize();

            // 顶栏只留一页（REQ-UI-096）：并入中央区的 details 与命令集只露一个，先把它显示出来。
            host.Show("details");
            AssertCenterTool(manager, "details");
            host.Dock("details", DockSide.Right, 0.3);
            host.ResetWindow("details");
            AssertCenterTool(manager, "details");
            host.SaveCurrentLayout();

            var recoveredManager = new DockingManager();
            var recoveredHost = new DockingHost(
                recoveredManager, descriptors, store, new NullLog());
            recoveredHost.Initialize();
            AssertCenterTool(recoveredManager, "details");
        });
    }

    [Fact]
    public void RestoredLayoutResolvesCenterTabTargetDeclaredAfterFollower()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var original = new DockingHost(
                new DockingManager(),
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            original.Initialize();
            original.SaveCurrentLayout();

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    new ToolWindowDescriptor
                    {
                        Id = "details",
                        Title = "details",
                        DefaultSide = DockSide.Tab,
                        DefaultTabTarget = "business",
                        DefaultRatio = 0.25,
                        ContentFactory = () => new Border(),
                    },
                    Tool("business", DockSide.Center, 1),
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                ],
                store,
                new NullLog());
            host.Initialize();

            // 顶栏只留一页：跟随页与目标都落在中央区，逐个显示出来验位置与身份。
            host.Show("details");
            AssertCenterTool(manager, "details");
            host.Show("business");
            Assert.Equal(DockSide.Center, host.ListWindows().Single(item => item.Id == "business").Side);
        });
    }

    [Fact]
    public void RuntimeRegistrationResolvesTabTargetDeclaredAfterFollower()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var host = new DockingHost(manager, [], new MemoryLayoutStore(), new NullLog());
            host.Initialize();

            host.RegisterWindow(new ToolWindowDescriptor
            {
                Id = "overview",
                Title = "项目总览",
                DefaultSide = DockSide.Tab,
                DefaultTabTarget = StandardWindowIds.Mcp,
                ContentFactory = () => new Border(),
            }, "HistoryJanus");
            Assert.Equal(DockSide.Right, host.ListWindows().Single(item => item.Id == "overview").Side);

            host.RegisterWindow(Tool(StandardWindowIds.Mcp, DockSide.Center, 1), "HistoryMercury");

            // 目标到了，跟随页并入中央区；顶栏只留一页，显示出来验。
            host.Show("overview");
            AssertCenterTool(manager, "overview");
        });
    }

    /// <summary>
    /// 布局存下之后才出现的中央页不能在布局里失踪。1.19.0 及以前它直接露面；
    /// 顶栏只留一页（REQ-UI-096）之后它可能被原来那一页挡着，契约改成「一次显示就到位」。
    /// </summary>
    [Fact]
    public void RestoredLayoutKeepsANewCenterPageOneShowAway()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var firstManager = new DockingManager();
            var firstHost = new DockingHost(
                firstManager,
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            firstHost.Initialize();
            firstHost.SaveCurrentLayout();

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                store,
                new NullLog());
            host.Initialize();

            Assert.Single(VisibleCenterIds(host));
            host.Show("business");
            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);
            // 业务中央页现在是工具窗口而非文档页：位置仍在主文档区，身份是 LayoutAnchorable。
            Assert.Contains(
                manager.Layout.Descendents().OfType<LayoutAnchorable>(),
                item => item.ContentId == "business" && item.Parent is LayoutDocumentPane);
        });
    }

    [Fact]
    public void CurrentLayoutPreservesHiddenCenterPageWithoutSettingsService()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var descriptors = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("business", DockSide.Center, 1),
            };
            var first = new DockingHost(
                new DockingManager(),
                descriptors,
                store,
                new NullLog());
            first.Initialize();
            first.Hide("business");
            first.SaveCurrentLayout();

            var manager = new DockingManager();
            var recovered = new DockingHost(manager, descriptors, store, new NullLog());
            recovered.Initialize();

            Assert.False(recovered.ListWindows().Single(item => item.Id == "business").IsVisible);
            Assert.DoesNotContain(
                manager.Layout.RootPanel.Descendents().OfType<LayoutDocument>(),
                item => item.ContentId == "business");
        });
    }

    [Fact]
    public void SaveCurrentLayoutStillPersistsPlacementsWhenLayoutWriteFails()
    {
        UiTestHost.RunSta(() =>
        {
            var settings = new MemorySettings();
            var host = new DockingHost(
                new DockingManager(),
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Right, 0.25),
                ],
                new FailingLayoutStore(),
                new NullLog(),
                settings);
            host.Initialize();
            host.Hide("business");

            host.SaveCurrentLayout();

            var json = settings.Get("layout.placements");
            Assert.False(string.IsNullOrWhiteSpace(json));
            using var document = JsonDocument.Parse(json!);
            Assert.True(document.RootElement.GetProperty("business").GetProperty("Hidden").GetBoolean());
        });
    }

    /// <summary>同上，走命名布局那条路：布局里没见过的中央页，一次显示就到位。</summary>
    [Fact]
    public void NamedLayoutKeepsACenterPageAddedAfterItWasSavedOneShowAway()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var original = new DockingHost(
                new DockingManager(),
                [Tool(StandardWindowIds.Mcp, DockSide.Center, 1)],
                store,
                new NullLog());
            original.Initialize();
            original.SaveLayout("before-business");

            var manager = new DockingManager();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("business", DockSide.Center, 1),
                ],
                store,
                new NullLog());
            host.Initialize();

            Assert.True(host.LoadLayout("before-business"));
            Assert.Single(VisibleCenterIds(host));
            host.Show("business");
            Assert.True(host.ListWindows().Single(item => item.Id == "business").IsVisible);
            // 同上：业务中央页以 LayoutAnchorable 呈现。
            Assert.Contains(
                manager.Layout.RootPanel.Descendents().OfType<LayoutAnchorable>(),
                item => item.ContentId == "business");
        });
    }

    [Fact]
    public void NonFiniteRatiosAreRejectedByDockingApiAndCommands()
    {
        UiTestHost.RunSta(() =>
        {
            var manager = new DockingManager();
            var log = new NullLog();
            var host = new DockingHost(
                manager,
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("details", DockSide.Right, 0.25),
                ],
                new MemoryLayoutStore(),
                log);
            host.Initialize();

            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => host.SetRatio("details", invalid));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => host.Dock("details", DockSide.Right, invalid));
            }

            var registry = new CommandRegistry();
            var bus = new CommandBus(registry, log);
            BuiltinCommands.Register(registry, new ShellCommandServices
            {
                Window = null!,
                Docking = host,
                Console = null!,
                History = null!,
                Settings = new MemorySettings(),
                Log = log,
                Bus = bus,
                DataDirectory = "",
            });

            foreach (var value in new[] { "NaN", "Infinity", "-Infinity" })
            {
                var dock = bus.ExecuteAsync(
                    $"aurora.ui.dock name=details pos=right ratio={value}",
                    "Test").GetAwaiter().GetResult();
                var ratio = bus.ExecuteAsync(
                    $"aurora.ui.ratio name=details value={value}",
                    "Test").GetAwaiter().GetResult();
                Assert.False(dock.Success);
                Assert.False(ratio.Success);
                Assert.Contains("严格位于 (0,1)", dock.Message, StringComparison.Ordinal);
                Assert.Contains("严格位于 (0,1)", ratio.Message, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void NewWindowOnRestoredLayoutKeepsDeclaredRatio()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryLayoutStore();
            var first = ShowHost(
                [Tool("existing", DockSide.Right, 0.25)], store, out var firstHost);
            try
            {
                UiTestHost.Pump();
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var second = ShowHost(
                [
                    Tool("existing", DockSide.Right, 0.25),
                    Tool("stage", DockSide.Top, 0.4),
                ], store, out var secondHost);
            try
            {
                UiTestHost.Pump();
                var ratio = secondHost.ListWindows().Single(item => item.Id == "stage").Ratio;
                Assert.NotNull(ratio);
                Assert.InRange(ratio.Value, 0.25, 0.55);
            }
            finally
            {
                second.Close();
            }
        });
    }

    [Fact]
    public void OpposingSidePanesAlwaysReserveTheCenterWorkspace()
    {
        UiTestHost.RunSta(() =>
        {
            var window = ShowHost(
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("left", DockSide.Left, 0.18),
                    Tool("right", DockSide.Right, 0.38),
                ], new MemoryLayoutStore(), out var host);
            try
            {
                UiTestHost.Pump();
                host.SetRatio("left", 0.55);
                host.SetRatio("right", 0.55);
                UiTestHost.Pump();

                var windows = host.ListWindows().ToDictionary(item => item.Id);
                var sides = windows["left"].Ratio!.Value + windows["right"].Ratio!.Value;
                Assert.InRange(sides, 0.48, 0.51);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MainWindowResizeDoesNotBecomeANewSplitterGesture()
    {
        UiTestHost.RunSta(() =>
        {
            var window = ShowHost(
                [
                    Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                    Tool("left", DockSide.Left, 0.2),
                    Tool("right", DockSide.Right, 0.3),
                ], new MemoryLayoutStore(), out var host);
            try
            {
                UiTestHost.Pump();
                var before = host.ListWindows().ToDictionary(item => item.Id);
                window.Width = 620;
                // DockingHost 的 _resizeDebounce 是 200ms：必须让它真正到期，
                // 才能断言「尺寸变化没有被当成拖动分隔条」的最终比例。
                UiTestHost.PumpFor(250);
                var after = host.ListWindows().ToDictionary(item => item.Id);

                Assert.InRange(
                    Math.Abs(after["left"].Ratio!.Value - before["left"].Ratio!.Value), 0, 0.03);
                Assert.InRange(
                    Math.Abs(after["right"].Ratio!.Value - before["right"].Ratio!.Value), 0, 0.03);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RestoredOversubscribedSidePanesAreNormalized()
    {
        UiTestHost.RunSta(() =>
        {
            var tools = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("left", DockSide.Left, 0.2),
                Tool("right", DockSide.Right, 0.3),
            };
            var store = new MemoryLayoutStore();
            var first = ShowHost(tools, store, out var firstHost);
            try
            {
                UiTestHost.Pump();
                var manager = (DockingManager)first.Content;
                foreach (var pane in manager.Layout.Descendents().OfType<LayoutAnchorablePane>()
                             .Where(pane => pane.Children.Any(item =>
                                 item.ContentId is "left" or "right")))
                {
                    pane.DockWidth = new GridLength(480, GridUnitType.Pixel);
                }
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var second = ShowHost(tools, store, out var secondHost);
            try
            {
                UiTestHost.Pump();
                UiTestHost.Pump();
                var windows = secondHost.ListWindows().ToDictionary(item => item.Id);
                var sides = windows["left"].Ratio!.Value + windows["right"].Ratio!.Value;
                Assert.InRange(sides, 0.48, 0.51);
            }
            finally
            {
                second.Close();
            }
        });
    }

    [Fact]
    public void RestoredSeparatePanesOnTheSameSideRemainSeparate()
    {
        UiTestHost.RunSta(() =>
        {
            var tools = new[]
            {
                Tool(StandardWindowIds.Mcp, DockSide.Center, 1),
                Tool("right.one", DockSide.Right, 0.25),
                Tool("right.two", DockSide.Right, 0.25),
            };
            var store = new MemoryLayoutStore();
            var first = ShowHost(tools, store, out var firstHost);
            try
            {
                UiTestHost.Pump();
                var manager = (DockingManager)first.Content;
                var second = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Single(item => item.ContentId == "right.two");
                var original = Assert.IsType<LayoutAnchorablePane>(second.Parent);
                original.Children.Remove(second);
                manager.Layout.RootPanel.Children.Add(new LayoutAnchorablePane(second)
                {
                    DockWidth = new GridLength(160, GridUnitType.Pixel),
                });
                firstHost.SaveCurrentLayout();
            }
            finally
            {
                first.Close();
            }

            var restored = ShowHost(tools, store, out _);
            try
            {
                UiTestHost.Pump();
                UiTestHost.Pump();
                var manager = (DockingManager)restored.Content;
                var right = manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .Where(item => item.ContentId is "right.one" or "right.two")
                    .ToArray();

                Assert.Equal(2, right.Length);
                Assert.NotSame(right[0].Parent, right[1].Parent);
            }
            finally
            {
                restored.Close();
            }
        });
    }

    private static Window ShowHost(
        IReadOnlyList<ToolWindowDescriptor> tools,
        MemoryLayoutStore store,
        out DockingHost host)
    {
        var manager = new DockingManager();
        host = new DockingHost(manager, tools, store, new NullLog());
        host.Initialize();
        var window = new Window
        {
            Width = 1000,
            Height = 700,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = manager,
        };
        window.Show();
        manager.UpdateLayout();
        return window;
    }

    /// <summary>
    /// 把中央区拆成左右两个文档区——等价于用户把一页拖到中央区侧边放下。
    /// 主文档区原样留着，<paramref name="id"/> 从它原来的地方搬进新拆出来的那一格。
    /// </summary>
    private static void SplitCenterArea(DockingManager manager, string id)
    {
        var content = manager.Layout.Descendents().OfType<LayoutContent>()
            .Single(item => item.ContentId == id);
        ((ILayoutContainer)content.Parent!).RemoveChild(content);
        var pane = manager.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>().First();

        var group = new LayoutDocumentPaneGroup { Orientation = Orientation.Horizontal };
        ((ILayoutContainer)pane.Parent!).ReplaceChild(pane, group);
        group.Children.Add(pane);
        group.Children.Add(new LayoutDocumentPane(content));
        manager.UpdateLayout();
    }

    private static void AssertCenterTool(DockingManager manager, string id)
    {
        var pane = Assert.Single(manager.Layout.Descendents().OfType<LayoutDocumentPane>());
        var anchorable = manager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Single(item => item.ContentId == id);
        Assert.Same(pane, anchorable.Parent);
        Assert.True(anchorable.CanDockAsTabbedDocument);
        Assert.DoesNotContain(
            manager.Layout.Descendents().OfType<LayoutDocument>(),
            item => item.ContentId == id);
    }

    private static ToolWindowDescriptor Tool(string id, DockSide side, double ratio) => new()
    {
        Id = id,
        Title = id,
        DefaultSide = side,
        DefaultRatio = ratio,
        ContentFactory = () => new Border(),
    };

    private static void AddFloating(
        LayoutRoot root,
        LayoutAnchorable content,
        double left,
        double top,
        double width,
        double height)
    {
        content.FloatingLeft = left;
        content.FloatingTop = top;
        content.FloatingWidth = width;
        content.FloatingHeight = height;
        var pane = new LayoutAnchorablePane(content);
        root.FloatingWindows.Add(new LayoutAnchorableFloatingWindow
        {
            RootPanel = new LayoutAnchorablePaneGroup(pane),
        });
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public void ReplaceCurrent(string oldValue, string newValue)
            => _current = _current?.Replace(oldValue, newValue, StringComparison.Ordinal);
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class FailingLayoutStore : ILayoutStore
    {
        public string? ReadCurrent() => null;
        public void WriteCurrent(string payload) => throw new IOException("simulated layout write failure");
        public void DeleteCurrent() { }
        public string? ReadNamed(string name) => null;
        public void WriteNamed(string name, string payload) => throw new IOException("simulated layout write failure");
        public IReadOnlyList<string> ListNamed() => [];
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
