using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
