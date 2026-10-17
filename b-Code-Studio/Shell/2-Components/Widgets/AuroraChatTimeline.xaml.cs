using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>REQ-UI-144：按方向排气泡，保留多行正文；阅读旧消息时刷新不抢走滚动位置。</summary>
public partial class AuroraChatTimeline : UserControl
{
    private string? _conversation;
    private bool _hasRows;
    private int _version;
    private long _readThrough;
    private long _notifiedThrough;
    public event Action<long>? ReadRequested;
    public AuroraChatTimeline()
    {
        AuroraComponentResources.Ensure(this);
        InitializeComponent();
        Loaded += (_, _) => UpdateVisibleImages();
        Unloaded += (_, _) => ResetImages();
        Latest.Click += (_, _) => Viewport.ScrollToEnd();
        Viewport.ScrollChanged += (_, _) =>
        {
            Latest.Visibility = Viewport.ScrollableHeight - Viewport.VerticalOffset > 24 ? Visibility.Visible : Visibility.Collapsed;
            NotifyRead();
            UpdateVisibleImages();
        };
        IsVisibleChanged += (_, _) => NotifyRead();
    }

    public void SetConversation(string? identity, string title, string subtitle)
    {
        Heading.Text = title;
        Subtitle.Text = subtitle;
        if (_conversation == identity) return;
        ResetImages();
        _conversation = identity;
        _notifiedThrough = 0;
        _hasRows = false;
        SetRows([], identity == null ? "选择左侧会话开始聊天" : "正在读取消息…");
    }

    public void SetRows(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string emptyText = "还没有消息")
    {
        var bottom = !_hasRows || Viewport.ScrollableHeight - Viewport.VerticalOffset < 24;
        var offset = Viewport.VerticalOffset;
        var version = ++_version;
        _readThrough = 0;
        _notifiedThrough = 0;
        if (rows.Count == 0) ResetImages();
        Messages.ItemsSource = rows.Select(row => new ChatMessageCard(row)).ToList();
        Empty.Text = emptyText;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hasRows = rows.Count > 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _version) return;
            if (bottom) Viewport.ScrollToEnd(); else Viewport.ScrollToVerticalOffset(offset);
            UpdateVisibleImages();
            // 等滚动布局真正落定后再确认已读；失败/空载荷不清红点。
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (version != _version) return;
                _readThrough = rows.Select(row => long.TryParse(row.GetValueOrDefault("ts_ms"), out var ts) ? ts : 0).DefaultIfEmpty().Max();
                NotifyRead();
            }));
        }));
    }

    private void NotifyRead()
    {
        if (!IsVisible || !_hasRows || _readThrough <= 0 || _readThrough == _notifiedThrough
            || Viewport.ScrollableHeight - Viewport.VerticalOffset > 24) return;
        _notifiedThrough = _readThrough;
        ReadRequested?.Invoke(_readThrough);
    }

    // REQ-UI-155：图片保持原比例，气泡无额外内边距；圆角裁切跟随实际图片尺寸。
    private void OnImageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var image = (Image)sender;
        var radius = image.TryFindResource("Aurora.Radius.Inner") is CornerRadius corner ? corner.TopLeft : 0;
        image.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }
}

internal sealed class ChatMessageCard(IReadOnlyDictionary<string, string> row)
{
    public IReadOnlyList<ChatImageCard> Images { get; } = ChatImageCard.Parse(row.GetValueOrDefault("images"));
    public string AttachmentSummary => row.GetValueOrDefault("attachment_summary") ?? "";
    public string MediaHint => row.GetValueOrDefault("media_hint") ?? "";
    public string Text => row.GetValueOrDefault("text") ?? "";
    public bool HasImage => Images.Count > 0;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasAttachmentSummary => !string.IsNullOrWhiteSpace(AttachmentSummary);
    public bool HasMediaHint => !string.IsNullOrWhiteSpace(MediaHint);
    public bool Outgoing => string.Equals(row.GetValueOrDefault("direction"), "out", StringComparison.OrdinalIgnoreCase);
    public string Meta => string.Join(" · ", new[] { row.GetValueOrDefault("who"), row.GetValueOrDefault("time") }.Where(s => !string.IsNullOrEmpty(s)));
}
