using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>
/// 选项条（REQ-UI-158）：候选一字排开，选中的那一项是墨色块，放不下就折行。
///
/// 与轮换选项框（<see cref="AuroraOptionBox"/>）是同一份候选、同一种取数，差别只在摆法：
/// 选项框把候选藏在右键菜单里，适合「看一眼当前值」；选项条把候选全摆在面上，
/// 右键因此空出来留给「对这一项做点什么」——由面板声明的菜单决定，组件自己不认识任何操作。
///
/// 每一项复用面板开关的样式（<c>Aurora.Panel.Switch</c>）：开启是墨色块，与「选中 / 主要」同一种语义，
/// 不为它另起一份模板。
/// </summary>
internal sealed class AuroraChoiceStrip : WrapPanel
{
    private readonly ObservableCollection<string> _values;
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private string? _selected;

    public AuroraChoiceStrip(ObservableCollection<string> values)
    {
        AuroraComponentResources.Ensure(this);
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _values = values;
        _values.CollectionChanged += OnValuesChanged;
        Render();
    }

    /// <summary>当前选中的值；没有候选时为 null。</summary>
    public string? SelectedValue
    {
        get => _selected;
        set
        {
            var next = value != null && _values.Contains(value) ? value : _values.FirstOrDefault();
            if (string.Equals(next, _selected, StringComparison.Ordinal))
            {
                SyncChecks();
                return;
            }

            _selected = next;
            SyncChecks();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>选中项变了（用户点选与程序回写都会发；区分由调用方的回写标记负责）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// 人在某一项上右键。由面板把声明的菜单挂上来；组件只负责报出是哪一项、在哪一个元素上。
    /// </summary>
    public Action<string, FrameworkElement>? ContextRequested { get; set; }

    /// <summary>候选的显示文字（行集里的 text 列）；没给的项显示值本身。</summary>
    public void SetTexts(IReadOnlyDictionary<string, string> texts)
    {
        _texts.Clear();
        foreach (var (value, text) in texts)
            _texts[value] = text;
        Render();
    }

    /// <summary>门禁用：当前摆出来的各项（按顺序）。</summary>
    internal IReadOnlyList<ToggleButton> Chips => Children.OfType<ToggleButton>().ToList();

    private void OnValuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Render();

    private void Render()
    {
        Children.Clear();
        foreach (var value in _values)
        {
            var chip = new ToggleButton
            {
                Content = _texts.TryGetValue(value, out var text) && text.Length > 0 ? text : value,
                Tag = value,
                Margin = new Thickness(0, 1, 4, 1),
                MinWidth = 48,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.SetResourceReference(StyleProperty, "Aurora.Panel.Switch");
            // 点一下是「选它」，不是开关：再点一次已选中的那一项不会把它取消。
            chip.Click += (_, _) => SelectedValue = value;
            chip.PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ContextRequested?.Invoke(value, chip);
            };
            Children.Add(chip);
        }

        // 选中项不在新候选里时由 SelectedValue 的 setter 退到第一项；这里只对齐勾选态。
        if (_selected != null && !_values.Contains(_selected))
            _selected = null;
        SyncChecks();
    }

    private void SyncChecks()
    {
        foreach (var chip in Children.OfType<ToggleButton>())
            chip.IsChecked = string.Equals(chip.Tag as string, _selected, StringComparison.Ordinal);
    }

    /// <summary>门禁用：模拟在某一项上右键。</summary>
    internal void RaiseContextFor(string value)
    {
        if (Chips.FirstOrDefault(chip => string.Equals(chip.Tag as string, value, StringComparison.Ordinal)) is { } chip)
            ContextRequested?.Invoke(value, chip);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // 吃掉按下：不吃的话外层（页面右键弹出层）会在同一次右键上再开一个菜单。
        e.Handled = true;
        base.OnPreviewMouseRightButtonDown(e);
    }
}
