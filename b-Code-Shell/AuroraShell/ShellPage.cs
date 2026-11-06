namespace AuroraShell;

/// <summary>
/// 壳唯一的页面：一个网页视图铺满，服务连不上时盖一层「正在连接」（REQ-SHELL-002）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>地址存在本机偏好里，缺省 <see cref="DefaultAddress"/>（电脑上的 Aurora Web）；连接层里可以改。</item>
/// <item>先探测再加载：探测不到就每 3 秒重试，不让网页视图停在浏览器自己的错误页上。</item>
/// <item>加载后若网页导航失败（服务中途退出），回到连接层继续探测。</item>
/// </list>
/// 这里只有连接逻辑。网页里的一切（布局、弹窗、主题……）都由 Aurora Web 自己负责，壳不认识它们。
/// </remarks>
public sealed class ShellPage : ContentPage
{
    internal const string DefaultAddress = "http://127.0.0.1:5280/";
    private const string AddressKey = "shell.address";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly WebView _web = new() { IsVisible = false };
    private readonly Grid _overlay;
    private readonly Label _status = new() { HorizontalTextAlignment = TextAlignment.Center, FontSize = 14 };
    private readonly Entry _address = new() { Keyboard = Keyboard.Url, FontSize = 14, MinimumWidthRequest = 280 };
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _connecting;

    public ShellPage()
    {
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#FBFAF7"), Color.FromArgb("#121414"));
        _status.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#38352F"), Color.FromArgb("#DAD7CE"));
        _address.SetAppThemeColor(Entry.TextColorProperty, Color.FromArgb("#38352F"), Color.FromArgb("#DAD7CE"));
        _address.Text = Preferences.Default.Get(AddressKey, DefaultAddress);
        _address.Completed += (_, _) => Connect(_address.Text);

        var connect = new Button { Text = "连接", FontSize = 14, CornerRadius = 4 };
        connect.SetAppThemeColor(Button.BackgroundColorProperty, Color.FromArgb("#26231E"), Color.FromArgb("#E2DAC6"));
        connect.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#FBFAF7"), Color.FromArgb("#121414"));
        connect.Clicked += (_, _) => Connect(_address.Text);

        _overlay = new Grid
        {
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 12,
                    Padding = 24,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _status, _address, connect },
                },
            },
        };

        _web.Navigated += (_, e) =>
        {
            if (e.Result is WebNavigationResult.Success)
                return;
            ShowOverlay($"页面加载失败，正在重试：{e.Url}");
            Connect(Preferences.Default.Get(AddressKey, DefaultAddress));
        };

        Content = new Grid { Children = { _web, _overlay } };
        Connect(_address.Text);
    }

    /// <summary>规范化地址：补协议、补结尾斜杠；不合法返回 null。</summary>
    internal static string? Normalize(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return null;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        return uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
    }

    private void Connect(string? text)
    {
        var address = Normalize(text);
        if (address == null)
        {
            ShowOverlay("地址无效：请填 http://主机:端口/");
            return;
        }
        Preferences.Default.Set(AddressKey, address);
        _address.Text = address;
        _connecting?.Cancel();
        var connecting = _connecting = new CancellationTokenSource();
        _ = ProbeThenLoadAsync(address, connecting.Token);
    }

    private async Task ProbeThenLoadAsync(string address, CancellationToken cancellation)
    {
        var attempt = 0;
        while (!cancellation.IsCancellationRequested)
        {
            attempt++;
            ShowOverlay(attempt == 1 ? $"正在连接 Aurora：{address}" : $"Aurora 服务未连接，第 {attempt} 次重试：{address}");
            try
            {
                using var response = await _probe.GetAsync(address, cancellation);
                if (response.IsSuccessStatusCode)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (cancellation.IsCancellationRequested)
                            return;
                        _web.Source = new UrlWebViewSource { Url = address };
                        _web.IsVisible = true;
                        _overlay.IsVisible = false;
                    });
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
            }
            try
            {
                await Task.Delay(RetryDelay, cancellation);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void ShowOverlay(string message)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            _status.Text = message;
            _overlay.IsVisible = true;
            _web.IsVisible = false;
        });
}
