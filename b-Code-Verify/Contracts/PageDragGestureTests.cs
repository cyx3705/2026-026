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
                new Border(),
                new Point(10, 10),
                new Point(12, 8),
                "modules");

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
                new Border(),
                new Point(10, 10),
                new Point(12, 8),
                "modules");

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
