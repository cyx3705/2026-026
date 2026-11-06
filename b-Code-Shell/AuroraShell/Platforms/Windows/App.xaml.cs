namespace AuroraShell.WinUI;

/// <summary>Windows 入口：MAUI 要求的 WinUI 应用对象，只转给 <see cref="MauiProgram"/>。</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
