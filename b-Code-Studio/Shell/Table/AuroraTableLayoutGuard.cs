namespace HistoryAurora.Shell.Table;

/// <summary>
/// 列宽分摊何时因外框宽度变化重来。
/// 不得订阅 ListView 自己的 <c>SizeChanged</c>：改列宽会反过来改 ListView 尺寸，
/// 振幅可达整页宽度，任何像素门槛都会被穿过，主进程单核打满，整窗假死。
/// </summary>
internal static class AuroraTableLayoutGuard
{
    public const int MaxWidthPasses = 5;
    public const double WidthEpsilon = 0.5;

    /// <summary>必须大于纵向滚动条宽度。</summary>
    public const double ExternalResizeEpsilon = 24;

    /// <summary>
    /// 同一轮抖动无论振幅多大，超过这个次数就停。
    /// 打开 Minerva 时列宽与停靠互相改尺寸，200px 级振荡也会把 24px 门槛打穿。
    /// </summary>
    public const int MaxRestartsPerBurst = 3;

    public static bool ShouldRestartWidthLayout(
        double widthDelta,
        int widthPasses,
        int consecutiveSizeChangeRestarts = 0,
        int restartsInBurst = 0)
    {
        if (restartsInBurst >= MaxRestartsPerBurst)
            return false;

        var delta = Math.Abs(widthDelta);
        if (delta < WidthEpsilon)
            return false;
        if (delta >= ExternalResizeEpsilon)
            return true;
        if (consecutiveSizeChangeRestarts >= MaxWidthPasses)
            return false;
        if (widthPasses > 0)
            return false;
        return true;
    }
}
