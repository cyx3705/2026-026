using System.IO;
using HistoryAurora.Shell.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 打开 Minerva 整窗假死：列宽回写会让 ListView 宽幅振荡，像素门槛挡不住。
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
    public void LargeAmplitudeOscillationStopsInsideOneBurst()
    {
        var burst = 0;
        var restarts = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var delta = i % 2 == 0 ? 400d : -400d;
            if (!AuroraTableLayoutGuard.ShouldRestartWidthLayout(
                    delta, widthPasses: 0, restartsInBurst: burst))
                continue;
            restarts++;
            burst++;
        }

        Assert.Equal(AuroraTableLayoutGuard.MaxRestartsPerBurst, restarts);
    }

    [Fact]
    public void WidthLayoutIgnoresListViewSizeChanged()
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
        Assert.Contains("SizeChanged += OnHostSizeChanged", table, StringComparison.Ordinal);
        Assert.DoesNotContain("_list.SizeChanged", table, StringComparison.Ordinal);
        Assert.Contains("_burstRestarts", table, StringComparison.Ordinal);
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
