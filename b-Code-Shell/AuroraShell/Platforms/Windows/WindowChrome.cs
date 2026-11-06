using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;

namespace AuroraShell;

/// <summary>
/// REQ-SHELL-005：电脑壳的窗口没有系统标题栏，弹出的窗口也没有标题栏和地址栏。
/// </summary>
/// <remarks>
/// <para>壳只提供一座通用的「窗口桥」，不认识网页内容：</para>
/// <list type="bullet">
/// <item>每个文档创建时注入 <c>globalThis.oneHistoryShell = { version: 1 }</c>，网页据此知道自己在壳里；</item>
/// <item>网页 <c>chrome.webview.postMessage({ oneHistoryWindow: "minimize" | "maximize" | "close" | "drag" })</c>，
/// 壳对发消息的那个窗口照做（maximize 在最大化与还原之间切换，drag 开始一次系统拖动）；</item>
/// <item>网页 <c>window.open</c> 打开的窗口由壳接成同样无边框的窗口（保留 opener 关系，浮窗搬 DOM 照常工作），
/// 网页 <c>window.close()</c> 时壳关掉对应窗口。</item>
/// </list>
/// 三个按钮放在哪、长什么样都由网页决定；壳只执行窗口动作。
/// </remarks>
internal static class WindowChrome
{
    private const string Marker = "globalThis.oneHistoryShell = Object.freeze({ version: 1 });";

    /// <summary>去掉系统标题栏，保留可拖边改大小的边框。</summary>
    public static void MakeFrameless(Microsoft.UI.Xaml.Window window)
    {
        window.ExtendsContentIntoTitleBar = false;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
    }

    /// <summary>给一个网页视图接上窗口桥。<paramref name="owner"/> 是它所在的 MAUI 窗口。</summary>
    public static async Task<CoreWebView2> AttachAsync(Microsoft.UI.Xaml.Controls.WebView2 view, Window owner)
    {
        await view.EnsureCoreWebView2Async();
        var core = view.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Marker);
        core.WebMessageReceived += (_, e) => OnMessage(owner, e);
        core.NewWindowRequested += (_, e) => OpenPopup(e);
        core.WindowCloseRequested += (_, _) => Application.Current?.CloseWindow(owner);
        return core;
    }

    private static void OnMessage(Window owner, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? verb;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            verb = message.RootElement.ValueKind == JsonValueKind.Object
                   && message.RootElement.TryGetProperty("oneHistoryWindow", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (owner.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window
            || window.AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        switch (verb)
        {
            case "minimize":
                presenter.Minimize();
                break;
            case "maximize":
                if (presenter.State == OverlappedPresenterState.Maximized)
                    presenter.Restore();
                else
                    presenter.Maximize();
                break;
            case "close":
                Application.Current?.CloseWindow(owner);
                break;
            case "drag":
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                ReleaseCapture();
                SendMessage(handle, WmNcLeftButtonDown, HtCaption, 0);
                break;
        }
    }

    private static async void OpenPopup(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var page = new PopupPage();
            var features = e.WindowFeatures;
            var popup = new Window(page) { Title = "OneHistory" };
            if (features.HasSize)
            {
                popup.Width = Math.Max(320, features.Width);
                popup.Height = Math.Max(240, features.Height);
            }
            if (features.HasPosition)
            {
                popup.X = features.Left;
                popup.Y = features.Top;
            }
            Application.Current!.OpenWindow(popup);
            e.NewWindow = await page.CoreReady;
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
