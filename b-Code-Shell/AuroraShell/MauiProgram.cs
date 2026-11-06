namespace AuroraShell;

/// <summary>壳的入口：只装一个页面，没有服务、没有插件。</summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        return builder.Build();
    }
}
