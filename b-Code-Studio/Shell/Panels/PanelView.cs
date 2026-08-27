using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.Themes;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Panels;

/// <summary>
/// 面板组件（REQ-UI-008）。按声明构建三种小组件——文字、文本框、按钮——
/// 外观全部由组件决定，声明侧只说「是什么」。
///
/// 按钮的落点是**动作 id**，不是指令：点击时向 <see cref="ActionRegistry"/> 现取声明，
/// 由声明给出真正的指令名与参数形状。模块改指令名只需改自己的声明，面板 JSON 一个字不动。
/// 反过来，动作没被声明时按钮**不渲染成按钮**，而是一块写着原因的警示牌——
/// 「点了没反应」是这一轮要消灭的东西，把它换成「看起来能点但其实不能」并没有变好。
/// </summary>
public sealed class PanelView : UserControl
{
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly ActionRegistry _actions;
    private readonly SelectionChannels? _channels;
    private readonly Dictionary<string, Func<string>> _getters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<string>> _setters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>跟随选择通道的文本框：通道名 → （列名, 控件 id）。</summary>
    private readonly List<(string Channel, string Column, string ControlId)> _followers = [];

    /// <summary>按选择通道启停的按钮。<c>ReadyTip</c> 是可用时的原提示，禁用时被原因顶掉。</summary>
    private readonly List<(string Channel, Button Button, string Reason, object? ReadyTip)> _gates = [];

    /// <summary>把自己的值发布到选择通道的文本框：通道名 → 控件 id（REQ-UI-045）。</summary>
    private readonly List<(string Channel, string ControlId)> _publishers = [];

    private readonly string _owner;

    private PanelDefinition _definition;

    /// <summary>
    /// 界面自持面板的 owner。通道台账按 owner 撤销（<see cref="SelectionChannels.DropOwner"/>），
    /// 而模块页面重载只撤模块自己那个 owner——用界面名登记，用户面板声明的通道
    /// 因此不会被某个模块的重载连带撤掉。
    /// </summary>
    public const string ShellOwner = "HistoryAurora";

    public PanelView(
        PanelDefinition definition,
        CommandBus bus,
        IShellLog log,
        ActionRegistry actions,
        SelectionChannels? channels = null,
        string? owner = null)
    {
        _bus = bus;
        _log = log;
        _actions = actions;
        _channels = channels;
        _owner = string.IsNullOrWhiteSpace(owner) ? ShellOwner : owner;
        _definition = definition;
        // 底色由窗格卡片提供，面板自身不再画一块白（UI 风格规范 §1）。
        Background = Brushes.Transparent;
        AuroraComponentResources.Ensure(this);

        // 只订阅一次，处理器读的是每轮 Rebuild 重填的 _followers / _gates。
        // 在 Rebuild 里订阅的话，每重建一次就多一个活着的处理器，旧那批还指着已经弃用的控件。
        if (_channels != null)
            _channels.Changed += OnSelectionChanged;

        Rebuild(definition);
    }

    /// <summary>
    /// 内容相对底板的内缩。与控制台过滤器工具条对齐：底板 Padding 2 + 这里 6，
    /// 首个控件的左边距因此落在 8px，和工具条里标签的 8px 左边距同一条线上。
    /// </summary>
    private static readonly Thickness PanelInset = new(6, 4, 6, 4);

    public string PanelId => _definition.Id;

    /// <summary>可反向驱动的控件 id 清单（aurora.ui.panelset 报错提示用）。</summary>
    public IReadOnlyList<string> ControlIds => _setters.Keys.ToList();

    /// <summary>按新声明原地重建内容（aurora.ui.panelreload，以及动作声明刷新后重绑按钮）。</summary>
    public void Rebuild(PanelDefinition definition)
    {
        _definition = definition;
        _getters.Clear();
        _setters.Clear();
        _followers.Clear();
        _gates.Clear();
        _publishers.Clear();

        var horizontal = string.Equals(definition.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        FrameworkElement content = horizontal
            ? BuildBoard(definition)
            : BuildColumn(definition);

        // 没有 ScrollViewer。面板就是一块面板：内容多了往下长，不往里滚。
        // 横排靠换行消化宽度，竖排本来就只长高度，两个方向都不需要滚动条。
        var surface = new Border { Child = content };
        surface.SetResourceReference(StyleProperty, "Aurora.Panel.Surface");
        Content = surface;

        // 引用按面板整体登记，重建即替换；断链账因此不会随重建越积越多。
        // 发布方不算引用：它是通道的源头，把自己记成引用会让台账里出现一条自引用的断链。
        _channels?.Reference(
            "面板 " + definition.Id,
            _followers.Select(f => f.Channel).Concat(_gates.Select(g => g.Channel)));

        DeclarePublishers();

        // 先按当前通道状态对齐一次：面板可能是在选中之后才建出来的
        // （窗口懒实例化、模块热重载），只等下一次 Changed 会让它停在一个空壳状态。
        SyncFromChannels(null);
    }

    /// <summary>
    /// 登记发布方并立刻把初值发上通道（REQ-UI-045）。
    ///
    /// **必须在建完全部控件之后做**：登记要读 <see cref="_getters"/>，而 getter 是建控件时才填上的。
    /// 也**必须发一次初值**——引用方（<c>switch</c> 容器、跟着通道取数的表格）
    /// 只在通道有值时才知道该显示哪一支；不发初值的话，页面一打开就停在"还没选"的空壳态，
    /// 而用户明明看见选项框里写着一个值。
    /// </summary>
    private void DeclarePublishers()
    {
        if (_channels is not { } channels)
            return;

        // 声明失败的那一条要从名单里去掉：留着的话它会继续往一个别人拥有的通道上发值，
        // 表现是"另一个面板的选项框莫名其妙自己变了"。
        for (var index = _publishers.Count - 1; index >= 0; index--)
        {
            var (channel, controlId) = _publishers[index];
            var origin = $"面板 {_definition.Id}#{controlId}";
            if (channels.TryDeclare(channel, _owner, origin, out var error))
                continue;

            _log.Error("panel", "面板 " + _definition.Id + ": " + error);
            _publishers.RemoveAt(index);
        }

        foreach (var (channel, controlId) in _publishers)
            PublishValue(channel, controlId);
    }

    /// <summary>把某个控件的当前值发上它声明的通道，列名固定 <c>value</c>。</summary>
    private void PublishValue(string channel, string controlId)
    {
        if (_channels == null || !_getters.TryGetValue(controlId, out var getter))
            return;

        _channels.Publish(
            channel,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["value"] = getter() });
    }

    /// <summary>
    /// 一行：一个主控件，外加零到多个声明了 <c>inline</c> 的同行按钮。
    ///
    /// 分组必须先于建控件做完。边建边判"下一个是不是同行按钮"的话，主控件已经按
    /// 两列布好，第三列只能事后塞——而 Grid 的列定义是建之前就要定的。
    /// </summary>
    private sealed record WidgetRow(PanelWidget Lead, List<PanelWidget> Inline);

    private static List<WidgetRow> GroupRows(IEnumerable<PanelWidget> widgets)
    {
        var rows = new List<WidgetRow>();
        foreach (var widget in widgets)
        {
            if (widget.ResolvedKind == PanelWidgetKind.Button && widget.Inline && rows.Count > 0)
                rows[^1].Inline.Add(widget);
            else
                rows.Add(new WidgetRow(widget, []));
        }

        return rows;
    }

    /// <summary>
    /// 同行按钮组。多个按钮并排放在同一格里，各自按内容宽，不铺满。
    /// 只有一个时也走这里：让"一个按钮"和"两个按钮"的边距是同一套。
    /// </summary>
    private FrameworkElement BuildInlineButtons(List<PanelWidget> widgets)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var widget in widgets)
        {
            var element = BuildButton(widget);
            // 同行按钮不铺满：铺满会把它撑成一条横杠，把旁边的输入框挤没。
            element.HorizontalAlignment = HorizontalAlignment.Right;
            element.Margin = new Thickness(8, 2, 0, 2);
            strip.Children.Add(element);
        }

        return strip;
    }

    /// <summary>竖排：一列，标签在左、控件在右、同行按钮在最右，行与行之间一条渐隐横线。</summary>
    private Grid BuildColumn(PanelDefinition definition)
    {
        var grid = new Grid { Margin = PanelInset };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 56 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 第三列给同行按钮。没有同行按钮时它宽度为 0，版面与两列时一模一样。
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rows = GroupRows(definition.Widgets);
        var row = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Build(grid, row, rows[index].Lead);

            if (rows[index].Inline.Count > 0)
            {
                var strip = BuildInlineButtons(rows[index].Inline);
                Grid.SetRow(strip, row);
                Grid.SetColumn(strip, 2);
                grid.Children.Add(strip);
            }

            row++;

            if (index >= rows.Count - 1)
                continue;

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var divider = new Border();
            divider.SetResourceReference(StyleProperty, "Aurora.Panel.Divider");
            Grid.SetRow(divider, row++);
            Grid.SetColumnSpan(divider, 3);
            grid.Children.Add(divider);
        }

        return grid;
    }

    /// <summary>
    /// 横排：控件铺开，一行放不下就换行，因此是多行多列而不是单排。
    /// 分隔线由 <see cref="AuroraPanelBoard"/> 按最终行列画出来，这里不放分隔件。
    ///
    /// 同行按钮跟主控件进同一格：换行时它们不会被拆到两行去，
    /// 「改名」落在下一行开头而它要改的框留在上一行，是看得见的错。
    /// </summary>
    private AuroraPanelBoard BuildBoard(PanelDefinition definition)
    {
        var board = new AuroraPanelBoard { Margin = PanelInset };

        foreach (var group in GroupRows(definition.Widgets))
        {
            var item = new Grid { VerticalAlignment = VerticalAlignment.Center };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Build(item, 0, group.Lead);

            if (group.Inline.Count > 0)
            {
                var strip = BuildInlineButtons(group.Inline);
                Grid.SetRow(strip, 0);
                Grid.SetColumn(strip, 2);
                item.Children.Add(strip);
            }

            board.Children.Add(item);
        }

        return board;
    }

    /// <summary>aurora.ui.panelset 落点：程序向面板控件回写值。</summary>
    public bool TrySetValue(string controlId, string value)
    {
        if (!_setters.TryGetValue(controlId, out var setter))
            return false;
        setter(value);
        return true;
    }

    // ---------------------------------------------------------------- 小组件构建

    private void Build(Grid grid, int row, PanelWidget widget)
    {
        switch (widget.ResolvedKind)
        {
            case PanelWidgetKind.Text:
                AddSpanning(grid, row, BuildText(widget));
                return;

            case PanelWidgetKind.Button:
                AddSpanning(grid, row, BuildButton(widget));
                return;

            case PanelWidgetKind.TextBox:
                AddLabelled(grid, row, widget, BuildTextBox(widget));
                return;

            default:
                // 校验器本该已经拦下；真漏到这里也要看得见，不静默跳过一行。
                AddSpanning(grid, row, Warning("无法识别的小组件: " + widget.Kind));
                return;
        }
    }

    private static FrameworkElement BuildText(PanelWidget widget)
    {
        // 换行而不是撑宽：面板按统一列宽排版，一段长文字不该把自己那一列顶出去
        // 压到相邻控件上（横排时表现为文字盖过分隔线）。
        var text = new TextBlock
        {
            Text = widget.Text ?? "",
            Margin = new Thickness(0, 4, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(StyleProperty, "Aurora.Panel.Text");
        return text;
    }

    private FrameworkElement BuildTextBox(PanelWidget widget)
    {
        var id = widget.Id!;
        if (widget.Follows is { Length: > 0 } follows
            && SelectionChannels.TrySplitBinding(follows, out var channel, out var column))
            _followers.Add((channel, column, id));

        var publish = widget.Channel is { Length: > 0 } declared ? declared : null;
        if (publish != null)
            _publishers.Add((publish, id));

        if (widget.ResolvedMode == PanelTextBoxMode.Select)
        {
            var options = widget.Options ?? [];
            var combo = new AuroraOptionBox { ItemsSource = options };
            combo.SetResourceReference(StyleProperty, "Aurora.Panel.OptionBox");
            combo.SelectedItem = widget.Value != null && options.Contains(widget.Value)
                ? widget.Value
                : options.FirstOrDefault();
            _getters[id] = () => combo.SelectedItem as string ?? "";
            _setters[id] = value => combo.SelectedItem =
                options.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (publish != null)
                combo.SelectionChanged += (_, _) => PublishIfDeclared(publish, id);
            return combo;
        }

        var box = new TextBox
        {
            Text = widget.Value ?? "",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(StyleProperty, "Aurora.Panel.Input");
        _getters[id] = () => box.Text;
        _setters[id] = value => box.Text = value;
        if (publish != null)
            box.TextChanged += (_, _) => PublishIfDeclared(publish, id);
        return box;
    }

    /// <summary>
    /// 控件值变了就发上通道——但只发**登记成功的那些**。
    ///
    /// 处理器挂在控件实例上，而登记是建完之后才做的：抢注失败的那一条如果照发，
    /// 就是往一个别人拥有的通道上写值，表现为"另一处的显示莫名其妙跟着我变"。
    /// </summary>
    private void PublishIfDeclared(string channel, string controlId)
    {
        if (_publishers.Any(item =>
                string.Equals(item.Channel, channel, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ControlId, controlId, StringComparison.OrdinalIgnoreCase)))
            PublishValue(channel, controlId);
    }

    private FrameworkElement BuildButton(PanelWidget widget)
    {
        var binding = _actions.Resolve(widget.Action);
        if (!binding.Ok)
        {
            // 声明缺失是面板作者与模块作者之间的事实不一致，必须当场说出来。
            _log.Error("panel", "面板 " + _definition.Id + ": " + binding.Error);
            return Warning(binding.Error!);
        }

        var action = binding.Action!;
        var button = new Button
        {
            Content = widget.Text ?? (action.Title.Length > 0 ? action.Title : action.Id),
            Margin = new Thickness(0, 6, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = string.IsNullOrWhiteSpace(action.Summary)
                ? action.Id + " → " + action.Command + "（由 " + action.Owner + " 声明）"
                : action.Summary,
        };
        button.SetResourceReference(
            StyleProperty,
            action.Danger ? "Aurora.Button.Danger" : "Aurora.Button.Ghost");
        button.Click += (_, _) => Fire(widget.Action!);

        if (widget.EnabledWhen?.Selected is { Length: > 0 } channel)
        {
            // 禁用态必须说得出原因。WPF 默认不给禁用控件弹提示，不开这一项的话
            // 「灰着的按钮」与「坏掉的按钮」在界面上完全一样。
            ToolTipService.SetShowOnDisabled(button, true);
            _gates.Add((channel, button, $"需要先在「{channel}」通道对应的表格里选中一行", button.ToolTip));
        }

        return button;
    }

    // ---------------------------------------------------------------- 选择通道接线

    private void OnSelectionChanged(object? sender, SelectionChannelChangedEventArgs e)
        => SyncFromChannels(e.Channel);

    /// <summary>
    /// 按通道当前状态刷新跟随框与按钮启停。<paramref name="only"/> 为 null 时刷新全部
    /// （面板刚建完），否则只碰这一个通道——别的通道没变，重刷会把用户正在改的名字冲掉。
    /// </summary>
    private void SyncFromChannels(string? only)
    {
        if (_channels == null)
            return;

        foreach (var (channel, column, controlId) in _followers)
        {
            if (only != null && !string.Equals(only, channel, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_setters.TryGetValue(controlId, out var setter))
                setter(_channels.Value(channel, column) ?? "");
        }

        foreach (var (channel, button, reason, readyTip) in _gates)
        {
            if (only != null && !string.Equals(only, channel, StringComparison.OrdinalIgnoreCase))
                continue;

            var ready = _channels.HasSelection(channel);
            button.IsEnabled = ready;
            button.ToolTip = ready ? readyTip : reason;
        }
    }

    /// <summary>缺件/断链的样子：看得见的一块，写清原因。</summary>
    private static FrameworkElement Warning(string reason)
    {
        var text = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(StyleProperty, "Aurora.Panel.Label");
        var border = new Border { Child = text, Margin = new Thickness(0, 6, 0, 2) };
        border.SetResourceReference(StyleProperty, "Aurora.Panel.Unbound");
        return border;
    }

    private static void AddSpanning(Grid grid, int row, FrameworkElement element)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, 0);
        // 跨到最后一列为止：少跨一列的症状是独占一行的按钮忽然只有半行宽，
        // 而那半行宽正好等于同行按钮列的宽度，看上去像是排版随机。
        Grid.SetColumnSpan(element, grid.ColumnDefinitions.Count);
        grid.Children.Add(element);
    }

    private static void AddLabelled(Grid grid, int row, PanelWidget widget, FrameworkElement input)
    {
        var label = new TextBlock { Text = widget.Label ?? widget.Id ?? "" };
        label.SetResourceReference(StyleProperty, "Aurora.Panel.Label");
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);

        input.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);

        grid.Children.Add(label);
        grid.Children.Add(input);
    }

    // ---------------------------------------------------------------- 按钮 → 动作 → 指令

    /// <summary>
    /// 点击时**现取**声明，而不是构建时把指令名固化进闭包：
    /// 模块热重载后声明会变，固化下来的那份就是下一个「点了没反应」。
    /// </summary>
    private void Fire(string actionId)
    {
        var binding = _actions.Resolve(actionId);
        if (!binding.Ok)
        {
            _log.Error("panel", "面板 " + _definition.Id + ": " + binding.Error);
            return;
        }

        foreach (var widget in _definition.Widgets)
        {
            if (widget.ResolvedKind != PanelWidgetKind.TextBox || !widget.Required)
                continue;
            if (widget.Id is not { } id || !_getters.TryGetValue(id, out var getter))
                continue;
            if (string.IsNullOrWhiteSpace(getter()))
            {
                _log.Error("panel", "面板 " + _definition.Id + ": " + (widget.Label ?? id) + " 为必填项");
                return;
            }
        }

        var text = ActionRegistry.BuildCommandText(
            binding.Action!,
            SelectionChannels.Chain(
                _channels,
                control => _getters.TryGetValue(control, out var getter) ? getter() : null),
            out var error);
        if (text == null)
        {
            _log.Error("panel", "面板 " + _definition.Id + ": " + error);
            return;
        }

        _ = _bus.ExecuteAsync(text, "UI");
    }
}
