using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Controls;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Docking;

/// <summary>
/// The only owner of the native window drag loop. Keeping capture release,
/// activation, DragMove and the docking probe together prevents a new gesture
/// path from silently bypassing AvalonDock's WM_MOVING pipeline.
/// </summary>
internal sealed class WindowDragDriver
{
    public void Start(Window hostWindow, DockingManager manager, IShellLog log, string source)
    {
        log.Info(source, $"开始拖动窗口 {hostWindow.GetType().Name}（浮窗={hostWindow is LayoutFloatingWindowControl}）");

        using var probe = hostWindow is LayoutFloatingWindowControl floating
            ? new DockingDragProbe(floating, manager, log, source)
            : null;
        probe?.Start();

        try
        {
            hostWindow.Activate();
            ReleaseMouseCapture();
            hostWindow.DragMove();
            log.Info(source, "窗口拖动结束");
        }
        catch (InvalidOperationException ex)
        {
            log.Warn(source, $"窗口拖动未启动：{ex.Message}");
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
