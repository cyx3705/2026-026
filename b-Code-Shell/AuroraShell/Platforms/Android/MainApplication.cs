using Android.App;
using Android.Runtime;

namespace AuroraShell;

/// <summary>Android 应用对象，只转给 <see cref="MauiProgram"/>。</summary>
[Application]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
