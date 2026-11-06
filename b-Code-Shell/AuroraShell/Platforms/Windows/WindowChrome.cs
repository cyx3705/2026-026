using System.Runtime.CompilerServices;
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

    /// <summary>
    /// 去掉系统标题栏，保留可拖边改大小的边框；再收起 MAUI 自己盖在内容顶部的 32px 标题行
    /// （<c>AppTitleBarContainer</c>，里面是窗口标题文字）。MAUI 会在映射标题时把它重新显示，
    /// 所以登记一个可见性回调把它钉成折叠。
    /// </summary>
    public static void MakeFrameless(Microsoft.UI.Xaml.Window window)
    {
        window.ExtendsContentIntoTitleBar = true;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        // Windows 10 上只关 WinUI 的标题栏，窗口仍带 WS_CAPTION，DWM 照样在顶部画一条 32px 的黑色标题区；
        // 直接去掉这个样式（保留 WS_THICKFRAME 以便拖边改大小）并让框架重算。
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = GetWindowLong(handle, GwlStyle);
        if ((style & WsCaption) != 0)
        {
            SetWindowLong(handle, GwlStyle, (style & ~WsCaption) | WsThickFrame);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        if (window.Content is not Microsoft.UI.Xaml.FrameworkElement root)
            return;
        void Pin()
        {
            if (Find(root, MauiTitleRow) is not { } row || row.Tag as string == Pinned)
                return;
            row.Tag = Pinned;
            row.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            row.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.UIElement.VisibilityProperty, (sender, _) =>
            {
                if (sender is Microsoft.UI.Xaml.UIElement element && element.Visibility != Microsoft.UI.Xaml.Visibility.Collapsed)
                    element.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            });
        }
        Pin();
        // 首次激活时 MAUI 的根视图模板可能还没展开，排到低优先级再找一次。
        root.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Pin);
    }

    private const string MauiTitleRow = "AppTitleBarContainer";
    private const string Pinned = "oneHistory.collapsed";

    private static Microsoft.UI.Xaml.FrameworkElement? Find(Microsoft.UI.Xaml.DependencyObject node, string name)
    {
        if (node is Microsoft.UI.Xaml.FrameworkElement element && element.Name == name)
            return element;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < count; index++)
            if (Find(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, index), name) is { } found)
                return found;
        return null;
    }

    /// <summary>
    /// 给一个网页视图接上窗口桥。<paramref name="host"/> 是承载它的 MAUI 页面；所在窗口在用到时才取，
    /// 因为弹出窗口的页面接上网页视图时，MAUI 还没把它挂到窗口上。
    /// </summary>
    public static async Task<CoreWebView2> AttachAsync(Microsoft.UI.Xaml.Controls.WebView2 view, Page host)
    {
        await view.EnsureCoreWebView2Async();
        var core = view.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Marker);
        core.WebMessageReceived += (_, e) => OnMessage(host.Window, e);
        core.NewWindowRequested += (_, e) => OpenPopup(e);
        core.WindowCloseRequested += (_, _) =>
        {
            if (host.Window is { } window)
                Application.Current?.CloseWindow(window);
        };
        return core;
    }

    private static void OnMessage(Window? owner, CoreWebView2WebMessageReceivedEventArgs e)
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

        if (owner?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window
            || window.AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        switch (verb)
        {
            case "minimize":
                presenter.Minimize();
                break;
            case "maximize":
                ToggleMaximize(window.AppWindow);
                break;
            case "close":
                Application.Current?.CloseWindow(owner);
                break;
            case "drag":
                FollowCursor(window);
                break;
        }
    }

    /// <summary>
    /// 拖动窗口：左键按住期间窗口跟着光标走，松开即停。
    /// 不用系统拖动（WM_SYSCOMMAND / WM_NCLBUTTONDOWN）：鼠标被网页视图所在的浏览器进程捕获着，系统的移动循环收不到移动。
    /// 从铺满状态拖动时先还原原来的大小，光标保持在标题行上相同的横向比例处。
    /// </summary>
    private static void FollowCursor(Microsoft.UI.Xaml.Window window)
    {
        var app = window.AppWindow;
        if (!GetCursorPos(out var start))
            return;
        var origin = app.Position;
        if (Restored.TryGetValue(app, out var previous))
        {
            Restored.Remove(app);
            var ratio = (start.X - origin.X) / (double)Math.Max(1, app.Size.Width);
            origin = new Windows.Graphics.PointInt32(start.X - (int)(previous.Value.Width * ratio), origin.Y);
            app.MoveAndResize(new Windows.Graphics.RectInt32(origin.X, origin.Y, previous.Value.Width, previous.Value.Height));
        }
        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(VkLeftButton) & 0x8000) == 0 || !GetCursorPos(out var now))
            {
                timer.Stop();
                return;
            }
            app.Move(new Windows.Graphics.PointInt32(origin.X + now.X - start.X, origin.Y + now.Y - start.Y));
        };
        timer.Start();
    }

    /// <summary>窗口铺满前的位置大小；在表里就表示「已铺满」。</summary>
    private static readonly ConditionalWeakTable<AppWindow, StrongBox<Windows.Graphics.RectInt32>> Restored = new();

    /// <summary>
    /// 铺满所在屏幕的工作区（不盖任务栏），再点还原到原来的位置大小。
    /// 不用系统最大化：去掉 WS_CAPTION 的窗口被系统最大化时会盖住任务栏。
    /// </summary>
    private static void ToggleMaximize(AppWindow window)
    {
        if (Restored.TryGetValue(window, out var previous))
        {
            Restored.Remove(window);
            window.MoveAndResize(previous.Value);
            return;
        }
        var bounds = new Windows.Graphics.RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
        Restored.Add(window, new StrongBox<Windows.Graphics.RectInt32>(bounds));
        window.MoveAndResize(DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea);
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

    private const int VkLeftButton = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);


}
