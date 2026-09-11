using System.IO;
using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Base.Docking;
using Xunit;
using HistoryAurora.Shell.Base;

namespace HistoryAurora.Verify;

public sealed class PageDragGestureTests
{
    [Fact]
    public void DockingDragSessionAllowsOnlyTheDeterministicLifecycle()
    {
        UiTestHost.RunSta(() =>
        {
            var session = new DockingDragSession(
                1,
                DockingDragKind.Tab,
                new Border(),
                new Point(10, 10),
                new Point(12, 8),
                "modules",
                null,
                "label:modules",
                false,
                false);

            Assert.True(session.TryTransition(DockingDragState.ThresholdReached));
            Assert.True(session.TryTransition(DockingDragState.FloatRequested));
            Assert.True(session.TryTransition(DockingDragState.FloatingReady));
            Assert.True(session.TryTransition(DockingDragState.WindowMoving));
            Assert.True(session.TryTransition(DockingDragState.Completed));
            Assert.False(session.TryTransition(DockingDragState.Cancelled));
        });
    }

    [Fact]
    public void DockingDragSessionRecordsEarlyReleaseWithoutCancellingFloatIntent()
    {
        UiTestHost.RunSta(() =>
        {
            var session = new DockingDragSession(
                2,
                DockingDragKind.Tab,
                new Border(),
                new Point(10, 10),
                new Point(12, 8),
                "modules",
                null,
                "label:modules",
                false,
                false);

            session.TryTransition(DockingDragState.ThresholdReached);
            session.TryTransition(DockingDragState.FloatRequested);
            session.MarkReleased(new Point(500, 300));

            Assert.True(session.ButtonReleased);
            Assert.Equal(new Point(500, 300), session.LastScreenPoint);
            Assert.Equal(DockingDragState.FloatRequested, session.State);
        });
    }

    [Fact]
    public void WindowDragSessionReachesMovingOnlyAfterThreshold()
    {
        UiTestHost.RunSta(() =>
        {
            var session = new DockingDragSession(
                3,
                DockingDragKind.Window,
                new Border(),
                new Point(10, 10),
                new Point(12, 8),
                null,
                new Window(),
                "floating:modules",
                false,
                false);

            Assert.True(session.TryTransition(DockingDragState.ThresholdReached));
            Assert.True(session.TryTransition(DockingDragState.WindowMoving));
            Assert.Equal(DockingDragState.WindowMoving, session.State);
        });
    }

    [Fact]
    public void WindowDragOperationsHaveOneOwner()
    {
        var root = FindSourceRoot();
        var coordinator = File.ReadAllText(
            Path.Combine(root, "b-Code-Studio", "Shell", "1-Base", "PageDragCoordinator.cs"));
        var driver = File.ReadAllText(
            Path.Combine(root, "b-Code-Studio", "Shell", "1-Base", "Docking", "WindowDragDriver.cs"));

        Assert.DoesNotContain(".DragMove(", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.Capture(null)", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseCapture(", coordinator, StringComparison.Ordinal);
        Assert.Contains(".DragMove(", driver, StringComparison.Ordinal);
        Assert.Contains("Mouse.Capture(null)", driver, StringComparison.Ordinal);
        Assert.Contains("ReleaseCapture(", driver, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-UI-101：顶栏整个删掉。拖动协调器里不再有页签按下、双击专注、页头拖窗口、
    /// AvalonDock 原生页签拖动这几条路——页面只剩 Ctrl 标签与右栏胶囊两种拖法。
    /// </summary>
    [Fact]
    public void TopBarGesturesAreGoneFromTheDragCoordinator()
    {
        var root = FindSourceRoot();
        var baseDir = Path.Combine(root, "b-Code-Studio", "Shell", "1-Base");
        Assert.False(File.Exists(Path.Combine(baseDir, "ShellTopBarCoordinator.cs")));
        Assert.False(File.Exists(Path.Combine(baseDir, "ShellTopBarCoordinator.NativeTabDrag.cs")));

        var coordinator = File.ReadAllText(Path.Combine(baseDir, "PageDragCoordinator.cs"))
                          + File.ReadAllText(Path.Combine(baseDir, "PageDragCoordinator.VisualTree.cs"));
        foreach (var gone in new[]
                 {
                     "DoubleClick", "aurora.ui.max", "PreviewMouseLeftButtonDownEvent", "ShellPaneHeader",
                     "_isMouseDown", "HandleDockTab", "HandleMain", "DispatcherTimer",
                 })
        {
            Assert.DoesNotContain(gone, coordinator, StringComparison.Ordinal);
        }

        Assert.Contains("FloatingWindowGeometry.GetCursorPosition()", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenThresholdDoesNotDependOnDockManagerOrTabOrigins()
    {
        var startScreen = new Point(1_000, 700);
        var currentScreen = new Point(1_014, 700);

        Assert.True(PageDragCoordinator.HasReachedDragThreshold(
            startScreen,
            currentScreen,
            horizontalThreshold: 4,
            verticalThreshold: 4));
        Assert.False(PageDragCoordinator.HasReachedDragThreshold(
            startScreen,
            new Point(1_003.99, 700),
            horizontalThreshold: 4,
            verticalThreshold: 4));
    }

    [Fact]
    public void MaximizedWindowRequiresTwiceTheSystemDragThreshold()
    {
        var start = new Point(10, 10);

        Assert.False(PageDragCoordinator.HasReachedDragThreshold(
            start, new Point(17.99, 10), 4, 4, multiplier: 2));
        Assert.True(PageDragCoordinator.HasReachedDragThreshold(
            start, new Point(18, 10), 4, 4, multiplier: 2));
        Assert.True(PageDragCoordinator.HasReachedDragThreshold(
            start, new Point(10, 18), 4, 4, multiplier: 2));
    }

    [Fact]
    public void DelayedDragRejectsOneHundredNineteenMilliseconds()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(gesture.Update(1_119, new Point(14, 10)));
        Assert.True(gesture.IsActive);
    }

    [Fact]
    public void DelayedDragAcceptsOneHundredTwentyMilliseconds()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.True(gesture.Update(1_120, new Point(14, 10)));
    }

    [Fact]
    public void DelayedDragRemembersEarlyMovementUntilHoldCompletes()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(gesture.Update(1_050, new Point(18, 10)));
        Assert.True(gesture.HasReachedThreshold);
        Assert.False(gesture.TryActivate(1_119));
        Assert.True(gesture.TryActivate(1_120));
    }

    [Fact]
    public void DelayedDragWaitsForMovementAfterHoldCompletes()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(gesture.TryActivate(1_120));
        Assert.False(gesture.Update(1_121, new Point(13.99, 10)));
        Assert.True(gesture.Update(1_122, new Point(14, 10)));
    }

    [Fact]
    public void DelayedDragCancellationPreventsActivation()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);
        gesture.Update(1_050, new Point(18, 10));

        gesture.Cancel();

        Assert.False(gesture.IsActive);
        Assert.False(gesture.HasReachedThreshold);
        Assert.False(gesture.TryActivate(1_120));
    }

    [Theory]
    [InlineData(WindowState.Normal, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowState.Normal)]
    [InlineData(WindowState.Minimized, WindowState.Maximized)]
    public void FloatingWindowToggleHasOneDeterministicTarget(
        WindowState current,
        WindowState expected)
        => Assert.Equal(expected, PageDragCoordinator.GetToggledWindowState(current));

    [Theory]
    [InlineData(96, 720, 520)]
    [InlineData(120, 900, 650)]
    [InlineData(144, 1080, 780)]
    public void FloatingPlacementConvertsDipSizeToMonitorPixels(
        double dpi,
        int expectedWidth,
        int expectedHeight)
    {
        var placement = FloatingWindowGeometry.CalculatePlacement(
            new Point(1_000, 500),
            new Rect(0, 0, 1_920, 1_080),
            new Size(720, 520),
            new Point(100, 20),
            dpi,
            dpi);

        Assert.Equal(expectedWidth, placement.Width);
        Assert.Equal(expectedHeight, placement.Height);
    }

    [Fact]
    public void FloatingPlacementKeepsPointerAtTheOriginalAnchor()
    {
        var placement = FloatingWindowGeometry.CalculatePlacement(
            new Point(1_000, 500),
            new Rect(0, 0, 1_920, 1_080),
            new Size(720, 520),
            new Point(120, 18),
            96,
            96);

        Assert.Equal(880, placement.Left);
        Assert.Equal(482, placement.Top);
    }

    [Fact]
    public void FloatingPlacementShrinksOnlyWhenLargerThanTheWorkArea()
    {
        var placement = FloatingWindowGeometry.CalculatePlacement(
            new Point(-500, 200),
            new Rect(-1_280, 0, 1_280, 720),
            new Size(2_000, 1_000),
            new Point(100, 20),
            96,
            96);

        Assert.Equal(1_280, placement.Width);
        Assert.Equal(720, placement.Height);
        Assert.Equal(-1_280, placement.Left);
        Assert.Equal(0, placement.Top);
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "b-Code-Studio",
                    "Shell",
                    "1-Base",
                    "Docking",
                    "WindowDragDriver.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 Aurora 源码根目录");
    }
}
