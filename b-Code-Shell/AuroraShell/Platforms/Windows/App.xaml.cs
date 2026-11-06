using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AuroraShell.WinUI;

/// <summary>Windows 入口：MAUI 要求的 WinUI 应用对象，只转给 <see cref="MauiProgram"/>。</summary>
public partial class App : MauiWinUIApplication
{
    // 进程活着就一直持有；第二个实例据此知道壳已经开着。
    private static Mutex? _single;

    public App()
    {
        ActivateRunningInstance();
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// REQ-SHELL-006：电脑壳单实例。宿主 <c>vulcan.app.show</c> 每次都直接拉起壳（Mercury 坞按钮、快捷键），
    /// 已开着时新进程只把旧窗口带到前台并退出，不另开一个窗口。
    /// </summary>
    private static void ActivateRunningInstance()
    {
        _single = new Mutex(true, @"Local\OneHistory.AuroraShell", out var first);
        if (first)
            return;

        var self = Environment.ProcessId;
        foreach (var peer in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
        {
            using (peer)
            {
                if (peer.Id == self || peer.MainWindowHandle == IntPtr.Zero)
                    continue;
                if (IsIconic(peer.MainWindowHandle))
                    ShowWindow(peer.MainWindowHandle, 9);
                SetForegroundWindow(peer.MainWindowHandle);
                break;
            }
        }
        Environment.Exit(0);
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
