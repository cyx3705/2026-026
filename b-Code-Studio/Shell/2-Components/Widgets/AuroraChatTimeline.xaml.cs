using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>REQ-UI-144：按方向排气泡，保留多行正文；阅读旧消息时刷新不抢走滚动位置。</summary>
public partial class AuroraChatTimeline : UserControl
{
    private string? _conversation;
    private bool _hasRows;
    private int _version;
    public AuroraChatTimeline()
    {
        AuroraComponentResources.Ensure(this);
        InitializeComponent();
        Latest.Click += (_, _) => Viewport.ScrollToEnd();
        Viewport.ScrollChanged += (_, _) => Latest.Visibility = Viewport.ScrollableHeight - Viewport.VerticalOffset > 24 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetConversation(string? identity, string title, string subtitle)
    {
        Heading.Text = title;
        Subtitle.Text = subtitle;
        if (_conversation == identity) return;
        _conversation = identity;
        _hasRows = false;
        SetRows([], identity == null ? "选择左侧会话开始聊天" : "正在读取消息…");
    }

    public void SetRows(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string emptyText = "还没有消息")
    {
        var bottom = !_hasRows || Viewport.ScrollableHeight - Viewport.VerticalOffset < 24;
        var offset = Viewport.VerticalOffset;
        var version = ++_version;
        Messages.ItemsSource = rows.Select(row => new ChatMessageCard(row)).ToList();
        Empty.Text = emptyText;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hasRows = rows.Count > 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _version) return;
            if (bottom) Viewport.ScrollToEnd(); else Viewport.ScrollToVerticalOffset(offset);
        }));
    }
}

internal sealed class ChatMessageCard(IReadOnlyDictionary<string, string> row)
{
    public string Text => row.GetValueOrDefault("text") ?? "";
    public bool Outgoing => string.Equals(row.GetValueOrDefault("direction"), "out", StringComparison.OrdinalIgnoreCase);
    public string Meta => string.Join(" · ", new[] { row.GetValueOrDefault("who"), row.GetValueOrDefault("time") }.Where(s => !string.IsNullOrEmpty(s)));
}
