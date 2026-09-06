using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 系统窗口移动循环的唯一入口。放掉捕获、激活、DragMove 收在一处,
/// 新的手势路径就不会绕过 AvalonDock 的 WM_MOVING 管线。
/// </summary>
internal sealed class WindowDragDriver
{
    public void Start(Window hostWindow)
    {
        try
        {
            hostWindow.Activate();
            ReleaseMouseCapture();
            hostWindow.DragMove();
        }
        catch (InvalidOperationException)
        {
            // 移动循环起不来时不必报警:调用方按拖动会话的终态自己收尾。
        }
        finally
        {
            ReleaseMouseCapture();
        }
    }

    public static void ReleaseMouseCapture(FrameworkElement? surface = null)
    {
        if (surface?.IsMouseCaptured == true)
            surface.ReleaseMouseCapture();
        Mouse.Capture(null);
        NativeMethods.ReleaseCapture();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();
    }
}
