using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.Themes;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Panels;

/// <summary>
/// 面板组件（REQ-UI-008）。按声明构建文字、文本框、按钮、开关和来源选择器。
/// 外观全部由组件决定，声明侧只说「是什么」和「哪几个是一行」。
///
/// 按钮的落点是**动作 id**，不是指令：点击时向 <see cref="ActionRegistry"/> 现取声明，
/// 由声明给出真正的指令名与参数形状。模块改指令名只需改自己的声明，面板 JSON 一个字不动。
/// 反过来，动作没被声明时按钮**不渲染成按钮**，而是一块写着原因的警示牌——
/// 「点了没反应」是这一轮要消灭的东西，把它换成「看起来能点但其实不能」并没有变好。
///
/// 版面全部交给 <see cref="AuroraPanelBoard"/>（REQ-UI-060）：本类只负责把一份声明
/// 翻译成「哪几个元素、各自最窄多宽、哪个可变」，怎么排、怎么折行、分隔线画在哪里，
/// 一行都不在这里。文本框和来源选择器保留独立标签；开关的描述文字由开关本体承载，
/// 避免再占一个额外的文字单元格。
/// </summary>
public sealed partial class PanelView : UserControl
{
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly ActionRegistry _actions;
    private readonly SelectionChannels? _channels;
    private readonly PageDataRefresher? _refresher;
    private readonly string? _pageId;
    private readonly Dictionary<string, Func<string>> _getters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<string>> _setters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>跟随选择通道的文本框：通道名 → （列名, 控件 id）。</summary>
    private readonly List<(string Channel, string Column, string ControlId)> _followers = [];

    /// <summary>按选择通道启停的按钮。<c>ReadyTip</c> 是可用时的原提示，禁用时被原因顶掉。</summary>
    private readonly List<(string Channel, Button Button, string Reason, object? ReadyTip)> _gates = [];

    /// <summary>把自己的值发布到选择通道的文本框：通道名 → 控件 id（REQ-UI-045）。</summary>
    private readonly List<(string Channel, string ControlId)> _publishers = [];

    /// <summary>候选项取数的选择框（REQ-UI-059）。</summary>
    private readonly List<OptionFeed> _feeds = [];

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
        string? owner = null,
        PageDataRefresher? refresher = null,
        string? pageId = null)
    {
        _bus = bus;
        _log = log;
        _actions = actions;
        _channels = channels;
        _refresher = refresher;
        _pageId = pageId;
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
    /// 内容相对底板的内缩（REQ-UI-061）。
    ///
    /// **上下是 0**，垂直方向的留白只有底板 <c>Aurora.Panel.Surface</c> 的 Padding 2——
    /// 与控制台顶部那条过滤器工具条完全一样，那条用的就是同一份底板样式、也没有额外内缩。
    /// 此前这里是 4，控件自己还各带 3～6 的上下边距，叠起来是 9～12，
    /// 于是同一份底板在控制台上薄薄一条、在控制面板里厚得像加了道边框。
    ///
    /// 左右保留 6：加上底板的 2 正好 8，与控制台工具条里标签的 8px 左边距同一条线。
    /// </summary>
    private static readonly Thickness PanelInset = new(6, 0, 6, 0);

    public string PanelId => _definition.Id;

    /// <summary>
    /// 可反向驱动的控件 id 清单。**只给 <c>aurora.ui.panelset</c> 的报错提示用**——
    /// 「没有这个控件」必须说得出有哪些控件，否则报错等于没报。
    /// 收成 internal：它不是面板对外的能力，是一句错误信息的素材（REQ-UI-060）。
    /// </summary>
    internal IReadOnlyList<string> ControlIds => _setters.Keys.ToList();

    /// <summary>按新声明原地重建内容（aurora.ui.panelreload，以及动作声明刷新后重绑按钮）。</summary>
    public void Rebuild(PanelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _definition = definition;
        _getters.Clear();
        _setters.Clear();
        _followers.Clear();
        _gates.Clear();
        _publishers.Clear();
        _feeds.Clear();

        var board = new AuroraPanelBoard { Margin = PanelInset };
        board.SetRows(definition.Rows.Select(BuildRow).ToList());

        // 没有 ScrollViewer。面板就是一块面板：内容多了往下长，不往里滚。
        // 宽度靠折行消化，所以横向永远不会溢出；纵向由行数决定，交给外层布局。
        var surface = new Border { Child = board };
        surface.SetResourceReference(StyleProperty, "Aurora.Panel.Surface");
        Content = surface;

        // 引用按面板整体登记，重建即替换；断链账因此不会随重建越积越多。
        // 发布方不算引用：它是通道的源头，把自己记成引用会让台账里出现一条自引用的断链。
        _channels?.Reference(
            "面板 " + definition.Id,
            _followers.Select(f => f.Channel)
                .Concat(_gates.Select(g => g.Channel))
                .Concat(_feeds.SelectMany(feed => feed.Channels)));

        DeclarePublishers();

        // 先按当前通道状态对齐一次：面板可能是在选中之后才建出来的
        // （窗口懒实例化、模块热重载），只等下一次 Changed 会让它停在一个空壳状态。
        SyncFromChannels(null);

        // 候选项取数排到下一拍：这一拍还在建控件，而取数会走总线、可能同步跑到底，
        // 中途回头改一个还没挂进可视树的选择框，选中项会被随后的初值覆盖掉。
        foreach (var feed in _feeds)
            QueueReload(feed);
    }

    private BoardRow BuildRow(PanelRow row)
    {
        var cells = new List<BoardCell>();
        foreach (var widget in row.Widgets)
            Build(cells, widget);

        return new BoardRow
        {
            // 校验器已经把无法识别的 mode 拦在门外；真漏到这里按缺省处理，不抛。
            Mode = row.ResolvedMode ?? PanelRowMode.Flex,
            Cells = cells,
        };
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

    private void Build(List<BoardCell> cells, PanelWidget widget)
    {
        switch (widget.ResolvedKind)
        {
            case PanelWidgetKind.Text:
                cells.Add(new BoardCell(BuildText(widget), widget.MinWidth, widget.Flex));
                return;

            case PanelWidgetKind.Button:
                cells.Add(new BoardCell(BuildButton(widget), widget.MinWidth, widget.Flex));
                return;

            case PanelWidgetKind.TextBox:
                // 标签是**独立的一个元素**（REQ-UI-060）：它参与最窄宽度的计算，
                // 也因此在它与输入区之间落下一条渐隐竖线。
                // 标签为空时不放这一格——空标签会画出一条紧贴左边、两侧什么都没有的线。
                if (Label(widget) is { Length: > 0 } text)
                    cells.Add(new BoardCell(BuildLabel(text), null, false));

                cells.Add(new BoardCell(
                    BuildTextBox(widget),
                    // 空输入框量不出宽度（内容宽度是 0），因此没声明就给一个够用的下限。
                    widget.MinWidth ?? AuroraPanelBoard.DefaultInputMinWidth,
                    widget.Flex));
                return;

            case PanelWidgetKind.Switch:
                var switchLabel = Label(widget);
                cells.Add(new BoardCell(
                    BuildSwitch(widget, switchLabel),
                    widget.MinWidth ?? (string.IsNullOrWhiteSpace(switchLabel) ? 48 : null),
                    widget.Flex));
                return;

            case PanelWidgetKind.SourcePicker:
                if (Label(widget) is { Length: > 0 } sourceLabel)
                    cells.Add(new BoardCell(BuildLabel(sourceLabel), null, false));
                cells.Add(new BoardCell(BuildSourcePicker(widget), widget.MinWidth ?? AuroraPanelBoard.DefaultInputMinWidth, widget.Flex));
                return;

            default:
                // 校验器本该已经拦下；真漏到这里也要看得见，不静默跳过一格。
                cells.Add(new BoardCell(Warning("无法识别的小组件: " + widget.Kind), null, false));
                return;
        }
    }

    private static string Label(PanelWidget widget) => widget.Label ?? widget.Id ?? "";

    private static FrameworkElement BuildLabel(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "Aurora.Panel.Label");
        return label;
    }

    private static FrameworkElement BuildText(PanelWidget widget)
    {
        // 换行而不是撑宽：面板按最窄宽度排版，一段长文字不该把自己那一格顶出去
        // 压到相邻元素上（表现为文字盖过分隔线）。
        var text = new TextBlock
        {
            Text = widget.Text ?? "",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
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
            // 候选是**可观察集合**：动态候选（REQ-UI-059）重取时就地换内容，
            // 不换实例。换实例的话，下面闭进 getter/setter 的那一份就成了旧的，
            // 表现为「程序回写的值明明在候选里，却选不中」。
            var options = new ObservableCollection<string>(widget.Options ?? []);
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

            if (widget.OptionsSource is { } source && !string.IsNullOrWhiteSpace(source.Command))
                _feeds.Add(new OptionFeed(id, combo, options, source, ChannelsOf(source), widget.Value));

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
        if (!string.IsNullOrWhiteSpace(widget.CommitAction))
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter)
                    return;
                e.Handled = true;
                Fire(widget.CommitAction!, new Dictionary<string, string> { ["value"] = box.Text });
            };
            box.LostFocus += (_, _) => Fire(widget.CommitAction!, new Dictionary<string, string> { ["value"] = box.Text });
        }
        return box;
    }

    private FrameworkElement BuildSwitch(PanelWidget widget, string label)
    {
        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            // 开关描述由控件本体承载；这样一个声明只对应一个排版单元格。
            Content = string.IsNullOrWhiteSpace(label) ? null : label,
            IsChecked = ParseBoolean(widget.Value),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.SetResourceReference(StyleProperty, "Aurora.Panel.Switch");
        _getters[widget.Id!] = () => (toggle.IsChecked == true).ToString().ToLowerInvariant();
        _setters[widget.Id!] = value => toggle.IsChecked = ParseBoolean(value);
        var action = widget.Action ?? widget.CommitAction;
        if (!string.IsNullOrWhiteSpace(action))
            toggle.Click += (_, _) => Fire(action, new Dictionary<string, string>
            {
                ["value"] = (toggle.IsChecked == true).ToString().ToLowerInvariant(),
            });
        return toggle;
    }

    private FrameworkElement BuildSourcePicker(PanelWidget widget)
    {
        var picker = new AuroraSourcePicker { Text = widget.Value ?? "" };
        picker.SetResourceReference(StyleProperty, "Aurora.Panel.Input");
        _getters[widget.Id!] = () => picker.Text;
        _setters[widget.Id!] = value => picker.Text = value;
        picker.CommitAsync = value =>
        {
            Fire(widget.CommitAction!, new Dictionary<string, string> { ["value"] = value });
            return Task.CompletedTask;
        };
        picker.SelectAsync = async () =>
        {
            try
            {
                var result = await _bus.ExecuteAsync(widget.SelectCommand!, "UI").ConfigureAwait(true);
                if (!result.Success)
                    return null;
                return result.Data as string;
            }
            catch (Exception ex)
            {
                _log.Log(ShellLogLevel.Warn, "panel", $"面板 {_definition.Id}: 来源选择失败 {ex.Message}");
                return null;
            }
        };
        return picker;
    }

    private static bool ParseBoolean(string? value)
        => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("开启", StringComparison.OrdinalIgnoreCase)
            || value.Equals("开", StringComparison.OrdinalIgnoreCase));

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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
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

    /// <summary>缺件/断链的样子：看得见的一块，写清原因。</summary>
    private static FrameworkElement Warning(string reason)
    {
        var text = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(StyleProperty, "Aurora.Panel.Label");
        var border = new Border { Child = text };
        border.SetResourceReference(StyleProperty, "Aurora.Panel.Unbound");
        return border;
    }

    // ---------------------------------------------------------------- 选择通道接线

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

    private void OnSelectionChanged(object? sender, SelectionChannelChangedEventArgs e)
        => SyncFromChannels(e.Channel);

    /// <summary>
    /// 按通道当前状态刷新跟随框、按钮启停与动态候选。<paramref name="only"/> 为 null 时刷新全部
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

        // 候选项取数只在**刚建完**（only == null）之外的路径上按通道过滤：
        // 上一级选了别的域，这一级的候选要跟着换一批，否则「类」里留着的是上一个域的类名。
        if (only == null)
            return;

        foreach (var feed in _feeds)
        {
            if (feed.Channels.Contains(only, StringComparer.OrdinalIgnoreCase))
                QueueReload(feed);
        }
    }

    // ---------------------------------------------------------------- 按钮 → 动作 → 指令

    /// <summary>
    /// 点击时**现取**声明，而不是构建时把指令名固化进闭包：
    /// 模块热重载后声明会变，固化下来的那份就是下一个「点了没反应」。
    /// </summary>
    private void Fire(string actionId, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var binding = _actions.Resolve(actionId);
        if (!binding.Ok)
        {
            _log.Error("panel", "面板 " + _definition.Id + ": " + binding.Error);
            return;
        }

        var text = ActionRegistry.BuildCommandText(
            binding.Action!,
            SelectionChannels.Chain(
                _channels,
                control => overrides != null && overrides.TryGetValue(control, out var value)
                    ? value
                    : _getters.TryGetValue(control, out var getter) ? getter() : null),
            out var error);
        if (text == null)
        {
            _log.Error("panel", "面板 " + _definition.Id + ": " + error);
            return;
        }

        _ = RunAndRefreshAsync(text);
    }

    /// <summary>
    /// 面板动作成功后必须把本页表格再取一遍。Minerva 选完来源文件后零件已经进了
    /// 模块内存，但表只在 Loaded 取过一次空结果——不刷新就会一直空着，整页像死了。
    /// </summary>
    private async Task RunAndRefreshAsync(string text)
    {
        var result = await _bus.ExecuteAsync(text, "UI").ConfigureAwait(true);
        if (!result.Success || _refresher == null || string.IsNullOrWhiteSpace(_pageId))
            return;
        _refresher.Refresh(_pageId, null);
    }
}
