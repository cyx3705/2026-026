using Microsoft.Maui.LifecycleEvents;

namespace AuroraShell;

/// <summary>壳的入口：只装一个页面，没有服务、没有插件；电脑上去掉系统标题栏。</summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if WINDOWS
        // REQ-SHELL-005：主窗口与弹出窗口都不要系统标题栏，窗口按钮由网页画、经窗口桥执行。
        builder.ConfigureLifecycleEvents(events => events.AddWindows(windows =>
            windows.OnWindowCreated(window => WindowChrome.MakeFrameless(window))));
#endif
        return builder.Build();
    }
}
