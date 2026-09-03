using System.Windows;
using System.Windows.Input;

namespace HistoryAurora.Shell.Base.Docking;

internal enum DockingDragKind
{
    Tab,
    Window,
}

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

internal sealed class DockingDragSession(
    long id,
    DockingDragKind kind,
    FrameworkElement surface,
    Point start,
    Point anchor,
    string? pageId,
    Window? hostWindow,
    string target,
    bool wasMaximized,
    bool requiresHold)
{
    public long Id { get; } = id;
    public DockingDragKind Kind { get; } = kind;
    public FrameworkElement Surface { get; } = surface;
    public Point Start { get; } = start;
    public Point Anchor { get; } = anchor;
    public string? PageId { get; } = pageId;
    public Window? HostWindow { get; } = hostWindow;
    public string Target { get; } = target;
    public bool WasMaximized { get; } = wasMaximized;
    public bool RequiresHold { get; } = requiresHold;
    public DockingDragState State { get; set; } = DockingDragState.Pressed;
    public bool ButtonReleased { get; set; }
    public Point LastScreenPoint { get; set; }
    public TaskCompletionSource<bool>? Completion { get; set; }

    public bool IsTab => Kind == DockingDragKind.Tab;

    public bool IsFloatingTab { get; init; }

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
            (DockingDragState.ThresholdReached, DockingDragState.WindowMoving) => true,
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
