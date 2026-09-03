using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using HistoryAurora.Shell.Composition;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace HistoryAurora.Verify;

/// <summary>
/// 鼠标冒烟：把一个浮出去的工具窗口**用真鼠标**拖回主窗体，合并成页签组。
///
/// 为什么必须用真鼠标：浮窗停靠的整条链路是
/// 「系统的窗口移动循环 → <c>WM_MOVING</c> → <c>UpdateDragPosition</c> → DragService →
/// 那组蓝色方位指示 → 松手 <c>WM_EXITSIZEMOVE</c> → Drop」。
/// 移动循环只有真的按下并移动才会启动，合成的 WPF 事件驱不动它——
/// 这就是这个缺陷此前在门禁里一直复现不出来的原因。
///
/// **默认不跑**：它会占用物理鼠标。设 <c>AURORA_MOUSE_SMOKE=1</c> 才执行。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class FloatingDockMouseSmoke(ITestOutputHelper output)
{
    private const string Switch = "AURORA_MOUSE_SMOKE";
    private const string TraceFileSwitch = "AURORA_MOUSE_SMOKE_TRACE_FILE";

    [Fact]
    public void DragAFloatingToolWindowBackIntoTheMainWindow()
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1")
        {
            output.WriteLine($"结果=输入未执行：这套冒烟会接管物理鼠标，需显式开启（set {Switch}=1）。");
            return;
        }

        var trace = new List<string>();
        var restore = MouseInput.Cursor();
        string? inputBlockReason = null;

        try
        {
            UiTestHost.RunSta(() =>
            {
                using var shell = Shell();
            var window = shell.Window;
            var manager = Single<DockingManager>(window);

            window.Docking.Float(StandardWindowIds.Modules);
            UiTestHost.PumpFor(600);

            var floating = Assert.Single(manager.FloatingWindows.OfType<LayoutFloatingWindowControl>().ToList());
            // 摆到主窗体**右侧之外**：两个窗口一旦重叠，合成输入打到的是 z 序更前的那个，
            // 抓取点就会落在主窗体上（实测踩过）。
            floating.Left = window.Left + window.Width + 40;
            floating.Top = window.Top + 120;
            floating.Width = 320;
            floating.Height = 220;
            UiTestHost.PumpFor(300);

            // 浮窗的内容住在**另一棵可视树**里（AvalonDock 的 FloatingWindowContentHost
            // 把它塞进一个子 HWND），从 LayoutFloatingWindowControl 往下走是走不到页头的。
            // 所以从模型侧的 Content 出发，先上溯到窗格控件，再在里面找页头。
            var model = manager.Layout.Descendents()
                .OfType<LayoutAnchorable>()
                .First(item => item.ContentId == StandardWindowIds.Modules);
            // 浮窗内容住在自己的 PresentationSource 里，从主窗体往下走是走不到的；
            // 从 model.Content 往上走也不行——它可能还挂在原来那棵树上。
            // 直接在本线程的所有 PresentationSource 里找承载这个模型的窗格控件。
            var paneControl = PresentationSource.CurrentSources
                .OfType<PresentationSource>()
                .Select(source => source.RootVisual)
                .OfType<DependencyObject>()
                .SelectMany(Descendants<LayoutAnchorablePaneControl>)
                .FirstOrDefault(control => control.Model is LayoutAnchorablePane pane
                                           && pane.Children.Contains(model));
            trace.Add($"窗格控件={(paneControl == null ? "(没找到)" : paneControl.GetType().Name)}");

            FrameworkElement? header = null;
            if (paneControl != null)
            {
                header = Descendants<FrameworkElement>(paneControl)
                    .FirstOrDefault(element => Equals(element.Tag, "ShellPaneHeader") && element.ActualWidth > 40);
                trace.Add($"找到 ShellPaneHeader={header != null}"
                          + (header == null ? "" : $" 尺寸={header.ActualWidth:F0}x{header.ActualHeight:F0}"));

                paneControl.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    var tagged = Ancestors(e.OriginalSource as DependencyObject)
                        .FirstOrDefault(item => item.Tag is string);
                    lock (trace)
                        trace.Add($"WPF 收到按下: {e.OriginalSource.GetType().Name}"
                                  + $"，最近的带 Tag 祖先={tagged?.Tag ?? "(无)"}");
                };
            }

            // 浮窗也要置顶并激活：合成输入打的是"屏幕那个点上最前面的窗口"，
            // 被别的应用盖住就全白搭。
            floating.Topmost = true;
            floating.Activate();
            UiTestHost.PumpFor(300);

            var grab = GrabPoint(header, floating);
            var drop = DropPoint(window);

            var floatingHandle = new System.Windows.Interop.WindowInteropHelper(floating).Handle;
            var mainHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            _ = SetForegroundWindow(floatingHandle);
            _ = BringWindowToTop(floatingHandle);
            UiTestHost.PumpFor(100);
            MouseInput.MoveTo(grab.X, grab.Y);
            Thread.Sleep(150);
            var underGrab = MouseInput.WindowAt(grab.X, grab.Y);
            var foreground = GetForegroundWindow();
            var floatingRect = GetWindowRect(floatingHandle);
            trace.Add($"抓取点下的窗口={(underGrab == IntPtr.Zero ? "无" : underGrab.ToString())}"
                      + $"（浮窗={floatingHandle}，主窗={mainHandle}，"
                      + $"命中浮窗={IsSameOrChild(underGrab, floatingHandle)}，"
                      + $"前台={foreground}，前台是浮窗={IsSameOrChild(foreground, floatingHandle)}，"
                      + $"浮窗矩形={floatingRect}，抓取点在矩形内={floatingRect.Contains(grab.X, grab.Y)}）");
            trace.Add($"grab={grab} drop={drop} floatingCount={manager.FloatingWindows.Count()}");

            if (!floatingRect.Contains(grab.X, grab.Y))
            {
                inputBlockReason = "输入未执行：浮窗抓取点落在可视屏幕之外，未进行物理拖动";
                trace.Add("结果=输入未执行（抓取点在浮窗矩形外）");
                return;
            }

            if (!IsSameOrChild(underGrab, floatingHandle))
            {
                inputBlockReason = "输入未执行：抓取点被其它窗口遮挡，未命中目标浮窗";
                trace.Add("结果=输入未执行（抓取点遮挡）");
                return;
            }

            if (!IsSameOrChild(foreground, floatingHandle))
            {
                inputBlockReason = "输入未执行：目标浮窗未成为前台窗口，系统不会把拖动消息交给它";
                trace.Add("结果=输入未执行（前台窗口不匹配）");
                return;
            }

            var sawDragging = false;
            var sawOverlay = false;
            var finished = false;

            // 不能靠轮询：拖动期间 UI 线程正卡在系统的模态移动循环里，
            // PumpUntil 的条件根本没机会被求值（第一版就是这么误判成 False 的）。
            // 挂属性变化，它在设值那一刻同步回调。
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(
                    LayoutFloatingWindowControl.IsDraggingProperty,
                    typeof(LayoutFloatingWindowControl))
                .AddValueChanged(floating, (_, _) => sawDragging |= floating.IsDragging);

            var driver = new Thread(() =>
            {
                try
                {
                    Drive(grab, drop);
                }
                catch (Exception ex)
                {
                    lock (trace)
                        trace.Add("驱动异常: " + ex.Message);
                }
                finally
                {
                    finished = true;
                }
            })
            {
                IsBackground = true,
            };

            driver.Start();
            UiTestHost.PumpUntil(
                () =>
                {
                    sawOverlay |= OverlayWindow(manager) != null;
                    return finished;
                },
                timeoutMilliseconds: 20_000);

            UiTestHost.PumpFor(800);

            trace.Add($"IsDragging 出现过={sawDragging}");
            trace.Add($"OverlayWindow 建过={sawOverlay}");
            trace.Add($"结束时浮窗数={manager.FloatingWindows.Count()}");
            trace.Add($"modules 是否回到主布局={InMainLayout(manager, StandardWindowIds.Modules)}");
            trace.Add($"与命令集同一窗格={SharesPaneWithMcp(manager)}");
            foreach (var entry in NullLog.Last.Snapshot()
                         .Where(item => item.Category.StartsWith("shell", StringComparison.Ordinal)
                                        || item.Category is "dock" or "layout"))
                trace.Add($"日志 [{entry.Category}] {entry.Message}");
            });
        }
        finally
        {
            MouseInput.MoveTo(restore.X, restore.Y);
        }

        foreach (var line in trace)
            output.WriteLine(line);

        var traceFile = Environment.GetEnvironmentVariable(TraceFileSwitch);
        if (!string.IsNullOrWhiteSpace(traceFile))
        {
            try
            {
                File.WriteAllLines(traceFile, trace);
                output.WriteLine($"冒烟追踪已写入={traceFile}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"冒烟追踪写入失败={ex.Message}");
            }
        }

        if (inputBlockReason != null)
        {
            output.WriteLine($"结果={inputBlockReason}；本次未驱动鼠标，也未执行链路断言。");
            return;
        }

        // 这三条按"链路从哪一环断掉"的顺序排，第一条红的就是断点。
        Assert.Contains(trace, line => line.StartsWith("日志 [shell.chrome] 开始拖动窗口", StringComparison.Ordinal));
        Assert.Contains(trace, line => line == "IsDragging 出现过=True");
        Assert.Contains(trace, line => line == "结束时浮窗数=0");
        Assert.Contains(trace, line => line == "modules 是否回到主布局=True");
        Assert.Contains(trace, line => line == "与命令集同一窗格=True");
    }

    // ---------------------------------------------------------------- 驱动与取点

    /// <summary>
    /// 一次像样的拖动：按下 → 先小幅越过阈值并停够 120ms（Aurora 的手势要求）→
    /// 再一路走到落点 → 停一会让指示层跟上 → 松手。
    /// </summary>
    private static void Drive((int X, int Y) grab, (int X, int Y) drop)
    {
        MouseInput.MoveTo(grab.X, grab.Y);
        Thread.Sleep(200);
        MouseInput.LeftDown();
        Thread.Sleep(120);

        MouseInput.MoveTo(grab.X + 8, grab.Y + 2);
        Thread.Sleep(120);
        MouseInput.MoveTo(grab.X + 18, grab.Y + 6);
        Thread.Sleep(200);

        MouseInput.DragPath((grab.X + 18, grab.Y + 6), drop, steps: 16, stepDelayMilliseconds: 40);
        Thread.Sleep(600);
        MouseInput.MoveTo(drop.X, drop.Y);
        Thread.Sleep(600);

        MouseInput.LeftUp();
        Thread.Sleep(400);
    }

    /// <summary>抓取点：窗格页头——Aurora 的拖动手势就挂在它身上。</summary>
    private static (int X, int Y) GrabPoint(FrameworkElement? header, LayoutFloatingWindowControl floating)
    {
        if (header != null)
        {
            // 躲开页签与那三个动作按钮，落在页头的空白处。
            var point = header.PointToScreen(new Point(header.ActualWidth / 2, header.ActualHeight / 2));
            return ((int)point.X, (int)point.Y);
        }

        var fallback = floating.PointToScreen(new Point(floating.ActualWidth / 2, 12));
        return ((int)fallback.X, (int)fallback.Y);
    }

    /// <summary>落点：中央文档区的正中——那里对应"合并成页签组"的中间方块。</summary>
    private static (int X, int Y) DropPoint(Window window)
    {
        var pane = Single<LayoutDocumentPaneControl>(window);
        var point = pane.PointToScreen(new Point(pane.ActualWidth / 2, pane.ActualHeight / 2));
        return ((int)point.X, (int)point.Y);
    }

    // ---------------------------------------------------------------- 结果判定

    private static object? OverlayWindow(DockingManager manager)
        => typeof(DockingManager)
            .GetField("_overlayWindow", System.Reflection.BindingFlags.Instance
                                        | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(manager);

    private static bool InMainLayout(DockingManager manager, string id)
        => manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Any(item => item.ContentId == id && !item.IsFloating);

    private static bool SharesPaneWithMcp(DockingManager manager)
    {
        var modules = manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(item => item.ContentId == StandardWindowIds.Modules);
        if (modules?.Parent is not ILayoutContainer pane)
            return false;
        return pane.Children
            .OfType<LayoutContent>()
            .Any(item => item.ContentId == StandardWindowIds.Mcp);
    }

    // ---------------------------------------------------------------- 装配

    private static ShellFixture Shell()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-mouse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var window = new ShellWindow(
            new ShellConfig
            {
                AppName = "HistoryAurora Mouse Smoke",
                AppVersion = "1.7.4",
                EnableRemoteManagementViews = true,
            },
            new MemoryLayoutStore(),
            new NullLog(),
            new MemorySettings(),
            dataDirectory)
        {
            // Keep the main and floating windows on-screen at 125%/150% DPI;
            // the old 1100-DIP fixture put the header's physical grab point
            // beyond the right edge on the development machine.
            Width = 820,
            Height = 760,
            Left = 60,
            Top = 60,
            ShowInTaskbar = false,
            Topmost = true,
        };
        window.Show();
        window.Activate();
        UiTestHost.PumpFor(600);
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

    /// <summary>合成输入命中的可能是浮窗里的子 HWND，因此要顺着父链比。</summary>
    private static bool IsSameOrChild(IntPtr candidate, IntPtr expected)
    {
        for (var current = candidate; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == expected)
                return true;
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    private static NativeRect GetWindowRect(IntPtr window)
        => GetWindowRect(window, out var rect) ? rect : default;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    private static IEnumerable<FrameworkElement> Ancestors(DependencyObject? source)
    {
        for (var current = source; current != null;)
        {
            if (current is FrameworkElement element)
                yield return element;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
    }

    private static T Single<T>(DependencyObject root)
        where T : DependencyObject
        => Assert.Single(Descendants<T>(root).ToList());

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
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

    /// <summary>记录 Aurora 自己的日志：拖动走到哪一步，全靠它说话。</summary>
    private sealed class NullLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public static NullLog Last { get; private set; } = new();

        public NullLog() => Last = this;

        public void Log(ShellLogLevel level, string category, string message)
        {
            lock (_entries)
                _entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));
        }

        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }

        public IReadOnlyList<ShellLogEntry> Snapshot()
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
