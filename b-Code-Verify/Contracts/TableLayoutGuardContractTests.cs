using System.IO;
using HistoryAurora.Shell.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 选完文件后主进程 CPU 打满：滚动条显隐约 17px，旧门槛 8px 会把列宽轮数清零死循环。
/// </summary>
public sealed class TableLayoutGuardContractTests
{
    [Fact]
    public void ScrollbarWidthIsBelowRestartThreshold()
    {
        Assert.True(AuroraTableLayoutGuard.ExternalResizeEpsilon > 17);
    }

    [Fact]
    public void FirstLayoutAlwaysRestarts()
    {
        Assert.True(AuroraTableLayoutGuard.ShouldRestartWidthLayout(17, widthPasses: 0));
    }

    [Fact]
    public void ScrollbarFlickerDoesNotClearPasses()
    {
        Assert.False(AuroraTableLayoutGuard.ShouldRestartWidthLayout(17, widthPasses: 1));
        Assert.False(AuroraTableLayoutGuard.ShouldRestartWidthLayout(-17, widthPasses: 3));
        Assert.False(AuroraTableLayoutGuard.ShouldRestartWidthLayout(17, widthPasses: 5));
    }

    [Fact]
    public void RealWindowResizeStillRestarts()
    {
        Assert.True(AuroraTableLayoutGuard.ShouldRestartWidthLayout(24, widthPasses: 5));
        Assert.True(AuroraTableLayoutGuard.ShouldRestartWidthLayout(200, widthPasses: 1));
        Assert.True(AuroraTableLayoutGuard.ShouldRestartWidthLayout(
            24, widthPasses: 5, consecutiveSizeChangeRestarts: 99));
    }

    [Fact]
    public void OscillatingScrollbarCannotSpin()
    {
        var passes = 1;
        var restarts = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var delta = i % 2 == 0 ? 17d : -17d;
            if (!AuroraTableLayoutGuard.ShouldRestartWidthLayout(delta, passes))
                continue;
            restarts++;
            passes = 0;
        }

        Assert.Equal(0, restarts);
    }

    [Fact]
    public void ClearedPassesStillCannotSpinForever()
    {
        var consecutive = 0;
        var restarts = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (!AuroraTableLayoutGuard.ShouldRestartWidthLayout(
                    17, widthPasses: 0, consecutiveSizeChangeRestarts: consecutive))
                continue;
            restarts++;
            consecutive++;
        }

        Assert.Equal(AuroraTableLayoutGuard.MaxWidthPasses, restarts);
    }

    [Fact]
    public void SourceCommitAndPageRefreshWaitUntilIdle()
    {
        var picker = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "Widgets", "AuroraSourcePicker.cs"));
        var panel = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "Panels", "PanelView.cs"));
        var table = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-Studio", "Shell", "Table", "AuroraTable.cs"));

        Assert.Contains(
            "DispatcherPriority.ApplicationIdle",
            picker,
            StringComparison.Ordinal);
        Assert.Contains(
            "DispatcherPriority.ApplicationIdle",
            panel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuroraTableLayoutGuard.ShouldRestartWidthLayout",
            table,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "const double ExternalResizeEpsilon = 8",
            table,
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("repository root not found");
    }
}
