using System.IO;
using System.Windows;
using HistoryAurora.Shell;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 双击页面标题栏聚焦（aurora.ui.max）对**每一个**已注册窗口都必须成立。
///
/// 真机报的是 `aurora.ui.max 执行异常(NotSupportedException)`，而本仓此前只覆盖了
/// 中央主文档那一个 id——工具窗口那条分支（LayoutAnchorablePane）一次都没跑过。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class MaximizeContractTests
{
    [Fact]
    public void EveryRegisteredWindowCanBeMaximizedAndRestored()
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-max-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var config = new ShellConfig
            {
                AppName = "HistoryAurora Max Test",
                AppVersion = "1.7.0",
                EnableRemoteManagementViews = true,
            };

            var window = new ShellWindow(
                config,
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

            try
            {
                window.Show();
                UiTestHost.Pump();

                var failures = new List<string>();
                foreach (var info in window.Docking.ListWindows().ToList())
                {
                    try
                    {
                        window.Docking.MaximizeWindow(info.Id);
                        UiTestHost.Pump();
                        Assert.Equal(info.Id, window.Docking.MaximizedId);
                        window.Docking.RestoreLayoutFromMaximized();
                        UiTestHost.Pump();
                        Assert.Null(window.Docking.MaximizedId);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{info.Id}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                Assert.Empty(failures);
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void MaximizeStillWorksWhileSomeWindowIsFloating()
    {
        // 真机上「只能拖出、叠不回去」，所以布局里几乎总有浮窗；
        // 双击聚焦的报错正是在那个状态下发生的。
        UiTestHost.RunSta(() =>
        {
            using var shell = Shell();
            var window = shell.Window;

            window.Docking.Float(StandardWindowIds.Modules);
            UiTestHost.Pump();

            var failures = new List<string>();
            foreach (var info in window.Docking.ListWindows().ToList())
            {
                try
                {
                    window.Docking.MaximizeWindow(info.Id);
                    UiTestHost.Pump();
                    window.Docking.RestoreLayoutFromMaximized();
                    UiTestHost.Pump();
                }
                catch (Exception ex)
                {
                    failures.Add($"{info.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.Empty(failures);
        });
    }

    [Fact]
    public void MaximizeWorksWithAModulePageInTheCentralArea()
    {
        // 真机上中央区里还有模块页（Mercury 的「扩展坞管理」）：它以**工具窗口**身份
        // 挂进主文档区，且 ContentFactory 返回的是同一个已经建好的元素实例
        // ——这正是 ModulePageLoader 的做法。
        UiTestHost.RunSta(() =>
        {
            using var shell = Shell();
            var window = shell.Window;

            var page = new System.Windows.Controls.Border();
            window.Docking.RegisterWindow(
                new ToolWindowDescriptor
                {
                    Id = "dock.manager",
                    Title = "扩展坞管理",
                    DefaultSide = DockSide.Center,
                    ContentFactory = () => page,
                },
                "HistoryMercury");
            window.Docking.Show("dock.manager");
            UiTestHost.Pump();

            var failures = new List<string>();
            foreach (var info in window.Docking.ListWindows().ToList())
            {
                try
                {
                    window.Docking.MaximizeWindow(info.Id);
                    UiTestHost.Pump();
                    window.Docking.RestoreLayoutFromMaximized();
                    UiTestHost.Pump();
                }
                catch (Exception ex)
                {
                    failures.Add($"{info.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.Empty(failures);
        });
    }

    [Fact]
    public void FloatingWindowsStayAttachedToTheLayoutThatIsCurrentlyMounted()
    {
        // 「只能拖出、叠不回去」的候选成因：整块替换 DockingManager.Layout
        // （最大化、恢复、按 XML 还原都会替换）之后，已经存在的浮窗的 Model.Root
        // 仍指向**旧的** LayoutRoot。拖回去时命中的是一棵已经不在界面上的树，
        // 于是看起来"拖回去没反应"。
        UiTestHost.RunSta(() =>
        {
            using var shell = Shell();
            var window = shell.Window;

            window.Docking.Float(StandardWindowIds.Modules);
            UiTestHost.Pump();

            var manager = RequireManager(window);
            var floating = manager.FloatingWindows.ToList();
            Assert.NotEmpty(floating);
            Assert.All(floating, item => Assert.Same(manager.Layout, item.Model.Root));

            window.Docking.MaximizeWindow(StandardWindowIds.Console);
            UiTestHost.Pump();
            window.Docking.RestoreLayoutFromMaximized();
            UiTestHost.Pump();

            var after = manager.FloatingWindows.ToList();
            // 空集合会让下面的 Assert.All 空转通过——浮窗"消失了"和"还在但接错树"
            // 是两个不同的缺陷，必须先分开。
            Assert.NotEmpty(after);
            Assert.All(
                after,
                item => Assert.Same(manager.Layout, item.Model.Root));
        });
    }

    [Fact]
    public void RestoreReturnsTheVerySameLayoutTreeInsteadOfRebuildingItFromXml()
    {
        // 聚焦与还原**不许经过 XML**。AvalonDock 随模块包装进可回收 AssemblyLoadContext
        // （Aurora 是 pinned:false），XmlSerializer 为其中的类型生成代码时报
        // 「非可回收程序集不能引用可回收程序集」——真机上双击页面标题栏因此
        // 报 NotSupportedException，而布局目录也一直是空的（退出前自动保存每次都失败）。
        // 测试进程把 AvalonDock 装在默认上下文里，复现不出那个异常，
        // 所以这条门禁改为钉住"还原拿回来的是同一棵树"这个可观察事实。
        UiTestHost.RunSta(() =>
        {
            using var shell = Shell();
            var manager = RequireManager(shell.Window);
            var before = manager.Layout;

            shell.Window.Docking.MaximizeWindow(StandardWindowIds.Console);
            UiTestHost.Pump();
            Assert.NotSame(before, manager.Layout);

            // 门禁进程里序列化是可用的，因此走的仍是 XML 快照那条路（浮窗能一起回来）。
            // 这里要钉住的是**那棵旧树被留住了**：宿主里 XML 不可用时靠它还原。
            Assert.NotNull(BeforeMaximizeRoot(shell.Window.Docking));

            shell.Window.Docking.RestoreLayoutFromMaximized();
            UiTestHost.Pump();
            Assert.Null(BeforeMaximizeRoot(shell.Window.Docking));
        });
    }

    private static object? BeforeMaximizeRoot(IDockingService docking)
        => docking.GetType()
            .GetField("_rootBeforeMaximize", System.Reflection.BindingFlags.Instance
                                             | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(docking);

    private static AvalonDock.DockingManager RequireManager(ShellWindow window)
        => Assert.Single(Descendants<AvalonDock.DockingManager>(window));

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

    private static ShellFixture Shell()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-max-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var window = new ShellWindow(
            new ShellConfig
            {
                AppName = "HistoryAurora Max Test",
                AppVersion = "1.7.0",
                EnableRemoteManagementViews = true,
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
        window.Show();
        UiTestHost.Pump();
        return new ShellFixture(window, dataDirectory);
    }

    private sealed class ShellFixture(ShellWindow window, string dataDirectory) : IDisposable
    {
        public ShellWindow Window { get; } = window;

        public void Dispose()
        {
            Window.Close();
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
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
