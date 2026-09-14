using System.Windows;

namespace HistoryAurora.Shell.Base.Docking;

internal enum DockingDragState
{
    Pressed,
    ThresholdReached,
    FloatRequested,
    FloatingReady,
    WindowMoving,
    Completed,
    Cancelled,
}

/// <summary>
/// 一次页面拖动：按下 → 过阈值 → 浮出载体 → 系统移动循环 → 落进一格或隐藏。
///
/// 1.22（REQ-UI-120）起只有页面这一种会话。此前还有「按住一个已经浮着的窗口整窗移动」
/// （<c>DockingDragKind.Window</c>，带宿主窗口与最大化标记），它只服务独立浮窗，随独立浮窗一起删除。
/// </summary>
internal sealed class DockingDragSession(
    long id,
    FrameworkElement surface,
    Point start,
    Point anchor,
    string pageId)
{
    public long Id { get; } = id;
    public FrameworkElement Surface { get; } = surface;
    public Point Start { get; } = start;
    public Point Anchor { get; } = anchor;
    public string PageId { get; } = pageId;
    public DockingDragState State { get; set; } = DockingDragState.Pressed;
    public bool ButtonReleased { get; set; }
    public Point LastScreenPoint { get; set; }
    public TaskCompletionSource<bool>? Completion { get; set; }

    public bool TryTransition(DockingDragState next)
    {
        if (State is DockingDragState.Completed or DockingDragState.Cancelled)
            return false;
        if (State == next)
            return true;

        var allowed = (State, next) switch
        {
            (DockingDragState.Pressed, DockingDragState.ThresholdReached) => true,
            (DockingDragState.Pressed, DockingDragState.Cancelled) => true,
            (DockingDragState.ThresholdReached, DockingDragState.FloatRequested) => true,
            (DockingDragState.ThresholdReached, DockingDragState.Cancelled) => true,
            (DockingDragState.FloatRequested, DockingDragState.FloatingReady) => true,
            (DockingDragState.FloatRequested, DockingDragState.Cancelled) => true,
            (DockingDragState.FloatingReady, DockingDragState.WindowMoving) => true,
            (DockingDragState.FloatingReady, DockingDragState.Completed) => true,
            (DockingDragState.FloatingReady, DockingDragState.Cancelled) => true,
            (DockingDragState.WindowMoving, DockingDragState.Completed) => true,
            (DockingDragState.WindowMoving, DockingDragState.Cancelled) => true,
            _ => false,
        };
        if (!allowed)
            return false;
        State = next;
        return true;
    }

    public bool IsLeftButtonDown =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VirtualKeyLeftButton) & 0x8000) != 0;

    public void MarkReleased(Point screenPoint)
    {
        ButtonReleased = true;
        LastScreenPoint = screenPoint;
    }

    private static class NativeMethods
    {
        internal const int VirtualKeyLeftButton = 0x01;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
    }
}
