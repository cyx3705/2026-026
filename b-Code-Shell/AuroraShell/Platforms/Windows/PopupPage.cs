using Microsoft.Web.WebView2.Core;

namespace AuroraShell;

/// <summary>
/// 网页 <c>window.open</c> 出来的窗口：只有一个网页视图，内容由打开它的网页通过 <see cref="CoreReady"/> 接管
/// （WebView2 的 NewWindow 交接，保留 opener 关系）。不设地址、不导航、没有地址栏。
/// </summary>
internal sealed class PopupPage : ContentPage
{
    private readonly WebView _web = new();
    private readonly TaskCompletionSource<CoreWebView2> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PopupPage()
    {
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#FBFAF7"), Color.FromArgb("#121414"));
        Content = _web;
        _web.HandlerChanged += async (_, _) =>
        {
            if (_web.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 view || _ready.Task.IsCompleted)
                return;
            try
            {
                _ready.TrySetResult(await WindowChrome.AttachAsync(view, this));
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
            }
        };
    }

    /// <summary>网页视图就绪、可交给打开者的那一刻。</summary>
    public Task<CoreWebView2> CoreReady => _ready.Task;
}
