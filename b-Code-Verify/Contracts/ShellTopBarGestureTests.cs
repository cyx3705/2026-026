using System.IO;
using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Docking;
using HistoryAurora.Shell;
using Xunit;

namespace HistoryAurora.Verify;

public sealed class ShellTopBarGestureTests
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
                "tab:modules",
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
                "tab:modules",
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
    public void WindowDragOperationsHaveOneOwner()
    {
        var root = FindSourceRoot();
        var coordinator = File.ReadAllText(
            Path.Combine(root, "b-Code-Studio", "Shell", "ShellTopBarCoordinator.cs"));
        var driver = File.ReadAllText(
            Path.Combine(root, "b-Code-Studio", "Shell", "Docking", "WindowDragDriver.cs"));

        Assert.DoesNotContain(".DragMove(", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.Capture(null)", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseCapture(", coordinator, StringComparison.Ordinal);
        Assert.Contains(".DragMove(", driver, StringComparison.Ordinal);
        Assert.Contains("Mouse.Capture(null)", driver, StringComparison.Ordinal);
        Assert.Contains("ReleaseCapture(", driver, StringComparison.Ordinal);
    }

    [Fact]
    public void TopBarUsesOneThresholdPathWithoutHoldTimerOrSecondTabRoute()
    {
        var root = FindSourceRoot();
        var coordinator = File.ReadAllText(
            Path.Combine(root, "b-Code-Studio", "Shell", "ShellTopBarCoordinator.cs"));

        Assert.DoesNotContain("DispatcherTimer", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("_hostDrag", coordinator, StringComparison.Ordinal);
        Assert.Contains("if (e.Handled)", coordinator, StringComparison.Ordinal);
        Assert.Contains("FloatingWindowGeometry.GetCursorPosition()", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenThresholdDoesNotDependOnDockManagerOrTabOrigins()
    {
        var startScreen = new Point(1_000, 700);
        var currentScreen = new Point(1_014, 700);

        Assert.True(ShellTopBarCoordinator.HasReachedDragThreshold(
            startScreen,
            currentScreen,
            horizontalThreshold: 4,
            verticalThreshold: 4));
        Assert.False(ShellTopBarCoordinator.HasReachedDragThreshold(
            startScreen,
            new Point(1_003.99, 700),
            horizontalThreshold: 4,
            verticalThreshold: 4));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void FloatingTabHeaderCanStartWindowDragOnlyInsideAFloatingWindow(
        bool isFloatingWindow,
        bool sourceIsTabItem,
        bool expected)
        => Assert.Equal(
            expected,
            ShellTopBarCoordinator.ShouldAllowFloatingTabWindowDrag(
                isFloatingWindow,
                sourceIsTabItem));

    [Fact]
    public void FastDoubleClickAcceptsExactlyTwoHundredFiftyMilliseconds()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.True(gesture.RegisterPress("header:console", 1_250, new Point(14, 14), 4, 4));
    }

    [Fact]
    public void FastDoubleClickRejectsTwoHundredFiftyOneMilliseconds()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.False(gesture.RegisterPress("header:console", 1_251, new Point(10, 10), 4, 4));
    }

    [Fact]
    public void FastDoubleClickRequiresTheSameTarget()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.False(gesture.RegisterPress("header:modules", 1_100, new Point(10, 10), 4, 4));
        Assert.True(gesture.RegisterPress("header:modules", 1_200, new Point(10, 10), 4, 4));
    }

    [Fact]
    public void FastDoubleClickRejectsMovementOutsideTheConfiguredRange()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.False(gesture.RegisterPress("header:console", 1_100, new Point(14.1, 10), 4, 4));
    }

    [Fact]
    public void DraggingCancelsThePendingDoubleClick()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        gesture.Cancel("header:console");
        Assert.False(gesture.RegisterPress("header:console", 1_100, new Point(10, 10), 4, 4));
    }

    [Fact]
    public void MaximizedWindowRequiresTwiceTheSystemDragThreshold()
    {
        var start = new Point(10, 10);

        Assert.False(ShellTopBarCoordinator.HasReachedDragThreshold(
            start, new Point(17.99, 10), 4, 4, multiplier: 2));
        Assert.True(ShellTopBarCoordinator.HasReachedDragThreshold(
            start, new Point(18, 10), 4, 4, multiplier: 2));
        Assert.True(ShellTopBarCoordinator.HasReachedDragThreshold(
            start, new Point(10, 18), 4, 4, multiplier: 2));
    }

    [Theory]
    [InlineData(true, WindowState.Normal, false)]
    [InlineData(true, WindowState.Maximized, true)]
    [InlineData(false, WindowState.Normal, true)]
    [InlineData(false, WindowState.Maximized, true)]
    public void OnlyNormalMainWindowSkipsTheTopBarHold(
        bool isMainWindow,
        WindowState state,
        bool expected)
        => Assert.Equal(
            expected,
            ShellTopBarCoordinator.ShouldDelayHostDrag(isMainWindow, state));

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

    [Fact]
    public void MovementBeforeTheHoldExpiresInvalidatesThePreviousClick()
    {
        var doubleClick = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));
        var drag = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        Assert.False(doubleClick.RegisterPress("header:main", 1_000, new Point(10, 10), 4, 4));
        drag.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(drag.Update(1_050, new Point(18, 10)));
        Assert.True(drag.HasReachedThreshold);
        doubleClick.Cancel("header:main");

        Assert.False(doubleClick.RegisterPress("header:main", 1_100, new Point(10, 10), 4, 4));
    }

    [Theory]
    [InlineData(WindowState.Normal, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowState.Normal)]
    [InlineData(WindowState.Minimized, WindowState.Maximized)]
    public void FloatingWindowToggleHasOneDeterministicTarget(
        WindowState current,
        WindowState expected)
        => Assert.Equal(expected, ShellTopBarCoordinator.GetToggledWindowState(current));

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
    public void FloatingPlacementKeepsPointerAtTheOriginalTabAnchor()
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
