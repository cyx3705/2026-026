namespace HistoryAurora.Shell.Table;

/// <summary>
/// 列宽分摊何时因 <c>SizeChanged</c> 重来。
/// Windows 纵向滚动条约 17 DIP。门槛若小于这个值，分摊一轮、滚动条一闪、
/// 轮数清零，列宽在两档之间空转，主进程单核打满，整窗假死。
/// </summary>
internal static class AuroraTableLayoutGuard
{
    public const int MaxWidthPasses = 5;
    public const double WidthEpsilon = 0.5;

    /// <summary>必须大于纵向滚动条宽度，对话框关闭后的闪动才不会清零轮数。</summary>
    public const double ExternalResizeEpsilon = 24;

    public static bool ShouldRestartWidthLayout(
        double widthDelta,
        int widthPasses,
        int consecutiveSizeChangeRestarts = 0)
    {
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
