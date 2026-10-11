using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>REQ-UI-143：通讯程序式会话卡片。选择用原始行发布，刷新按 id 保留选中，避免新消息打断聊天。</summary>
public partial class AuroraConversationList : UserControl
{
    private bool _updating;
    public event EventHandler? SelectionChanged;
    public IReadOnlyDictionary<string, string>? SelectedRow => (Conversations.SelectedItem as ConversationCard)?.Row;

    public AuroraConversationList()
    {
        AuroraComponentResources.Ensure(this);
        InitializeComponent();
        Conversations.SelectionChanged += (_, _) => { if (!_updating) SelectionChanged?.Invoke(this, EventArgs.Empty); };
    }

    public void SetRows(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string emptyText = "暂时没有会话")
    {
        var selected = SelectedRow?.GetValueOrDefault("id");
        var previousFirst = (Conversations.Items.Cast<ConversationCard>().FirstOrDefault())?.Row.GetValueOrDefault("id");
        var cards = rows.Select(row => new ConversationCard(row)).ToList();
        _updating = true;
        try
        {
            Conversations.ItemsSource = cards;
            Conversations.SelectedItem = selected == null ? null : cards.FirstOrDefault(card => card.Row.GetValueOrDefault("id") == selected);
            Empty.Text = emptyText;
            Empty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updating = false; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        // REQ-UI-149：数据源把新消息会话排到首位后，露出首行但不改变聊天目标。
        if (cards.Count > 0 && previousFirst != cards[0].Row.GetValueOrDefault("id"))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (ReferenceEquals(Conversations.ItemsSource, cards))
                    FindViewport(Conversations)?.ScrollToTop();
            }));
    }

    private static ScrollViewer? FindViewport(DependencyObject parent)
    {
        if (parent is ScrollViewer viewport) return viewport;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindViewport(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}

internal sealed class ConversationCard(IReadOnlyDictionary<string, string> row)
{
    public IReadOnlyDictionary<string, string> Row { get; } = row;
    public string Title => Value("title", "未命名会话");
    public string Avatar => StringInfo.GetNextTextElement(Title);
    public string Preview => Value("last", "暂无消息");
    public string Source => string.Join(" · ", new[] { Value("source"), Value("kind") }.Where(s => s.Length > 0));
    public string When => Value("when");
    public string Unread => int.TryParse(Value("unread"), out var count) && count > 0 ? count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture) : "";
    private string Value(string key, string fallback = "") => Row.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : fallback;
}
