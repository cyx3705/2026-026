using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Components.Graph;
using HistoryAurora.Shell.Components.Panels;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Neutral;

namespace HistoryAurora.Shell.Components.Pages;

/// <summary>渲染一页所需的外部依赖。</summary>
public sealed class PageRenderContext
{
    public required CommandBus Bus { get; init; }

    public required IShellLog Log { get; init; }

    /// <summary>提供方模块名，用于日志归因。</summary>
    public required string Owner { get; init; }

    /// <summary>
    /// 动作声明台账。为 null 时按钮与泳道的动作绑定一律解析失败并渲染成显式占位——
    /// 静默变成不可点的按钮不算"降级"，那正是本轮要消灭的形态。
    /// </summary>
    public ActionRegistry? Actions { get; init; }

    /// <summary>
    /// 补全候选的来源（REQ-UI-013）。为 null 时声明了 <c>suggest</c> 的输入框
    /// 退回普通输入框并记一条 Warn——静默地少掉补全，正是"只有这一页不一样"的老形态。
    /// </summary>
    /// <summary>
    /// 补全会话。1.8.14 起页面这一层**没有节点消费它**——带补全的输入框随
    /// <c>input</c> 节点一同退役（见 <see cref="PageRenderer.RetiredComponents"/>）。
    /// 字段保留：控制面板日后要接补全时，取数口就在这里，调用方也已经在传了。
    /// </summary>
    public AuroraCompletionProvider? Completions { get; init; }

    /// <summary>
    /// 选择通道台账（REQ-UI-041）。表格按 <c>channel</c> 把选中行发上通道，
    /// 控制面板按通道名取值——**这是页面之间唯一的接线方式**，页内节点 id 不跨页。
    /// 为 null 时表格的 <c>channel</c> 声明记一条 Warn 并忽略。
    /// </summary>
    public SelectionChannels? Channels { get; init; }

    /// <summary>
    /// 取数刷新台账（REQ-UI-044）。为 null 时取数仍在 <c>Loaded</c> 跑一次，
    /// 但此后既不会跟着选中变化重取，也不接受 <c>aurora.ui.refreshdata</c>。
    /// </summary>
    public PageDataRefresher? Refresher { get; init; }

    /// <summary>
    /// 列序记忆（REQ-UI-062）。为 null 时列照样拖得动，只是重开页面回到声明顺序。
    /// </summary>
    public IColumnOrderStore? ColumnOrder { get; init; }
}

/// <summary>渲染结果。缺件被记录下来而不是丢弃，供缺件清单查询。</summary>
public sealed class RenderedPage
{
    public required FrameworkElement Root { get; init; }

    /// <summary>描述里引用了但组件库尚未提供的组件类型。</summary>
    public required IReadOnlyList<string> MissingComponents { get; init; }
}

/// <summary>
/// 把页面描述渲染成 WPF 界面。**这是模块与 WPF 之间唯一的边界**：
/// 模块只提交数据，控件全部由本类用 Aurora 组件库构造，因此模块无从自建组件（DEC-005）。
///
/// 三条硬规则：
/// <list type="bullet">
///   <item>描述里不接受任何样式键或颜色，只接受语义档位，映射表写死在本类里；</item>
///   <item>未知组件渲染为**显式占位**并计入缺件清单，绝不静默省略——
///         静默省略正是 <c>DynamicResource</c> 时代的失败形态，改协议就是为了消灭它；</item>
///   <item>复合组件（表格、面板、泳道）不在本类里现拼控件，一律委派给组件本体。
///         本类只做"描述 → 组件输入"的翻译，外观归组件（REQ-UI-007/008/010）。</item>
/// </list>
/// </summary>
public static partial class PageRenderer
{
    /// <summary>
    /// 间距不走 DynamicResource：间距令牌在浅色与深色里取值相同（PadTight=8 / Pad=12），
    /// 不随主题变化；而 Thickness 资源是四边统一值，用作 Margin 会给纵向栈额外撑出左右缩进。
    /// 画刷与文字样式仍然走 DynamicResource，主题切换必须跟随。
    /// </summary>
    private const double GapTight = 8;

    private const double GapNormal = 12;

    /// <summary>
    /// V1 组件集。这是「某个申请是否已交付」的**唯一权威**——台账不另存一份状态，
    /// 否则两边会各说各话。新增组件时改这里与 <see cref="Build"/> 的分派，两处必须同步。
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedComponents =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "stack", "text", "table", "panel", "swimlane", "grid", "popup", "switch",
        };

    /// <summary>
    /// 曾经是页面节点、现已退役的类型。
    ///
    /// 小型交互控件（按钮、输入框、下拉）**只能出现在控制面板里**：页面这一层只放
    /// 容器、展示组件和复合组件。散落在页面各处的单个控件没有共同的排版依据，
    /// 每加一个就要重新决定它跟谁对齐、跟谁分组。
    ///
    /// 退役与"缺件"是两回事，因此**不进** <see cref="RenderedPage.MissingComponents"/>：
    /// 缺件会被 <c>ModulePageLoader</c> 自动记进组件申请台账，
    /// 把退役类型也记进去等于让模块不断申请一个已经决定不给的东西。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredComponents =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["button"] = "按钮",
            ["input"] = "输入框",
            ["select"] = "下拉框",
        };

    /// <summary>
    /// 已交付的**能力**：它们不是独立的节点类型，而是挂在某个节点上的字段
    /// （表格的 <c>rowActions</c>、输入框的 <c>suggest</c>）。
    ///
    /// 单独列一份，是因为组件申请台账按名字销账：Mercury 申请的是
    /// <c>table.rowactions</c> / <c>menu</c> / <c>input.suggest</c> 这样的名字，
    /// 而把它们塞进 <see cref="SupportedComponents"/> 会让
    /// <c>type: "table.rowactions"</c> 一边被判为"已支持"、一边渲染成缺件占位。
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "table.rowactions", "table.cellaction", "dialog.choice", "menu",
            // REQ-UI-041：表格发布选中行、面板跟随取值与按选中启停。
            // 「表格选中行 → 按钮变可用」这条链路 1.8.14 随页面按钮一起没了，这三条把它接回来，
            // 落点从页内节点 id 换成界面级通道，因此顺带能跨页。
            "table.channel", "panel.follows", "panel.enabledwhen",
            // REQ-UI-060：面板的行是声明出来的一等结构，行内可以指定均布或可变宽度，
            // 元素可以注册自己的最窄宽度。它取代了 REQ-UI-043 的 panel.inline——
            // 「与前一个控件同行」在有了真正的行之后没有存在的余地。
            "panel.rows", "panel.minwidth", "panel.flex", "panel.icon",
            // REQ-UI-059：选择框的候选来自一条只读指令，可跟着通道重取（两级联动下拉）。
            "panel.optionssource",
            // REQ-UI-065：极简开关、来源选择器及输入提交动作。
            "panel.switch", "panel.sourcepicker", "panel.commitaction",
            // REQ-UI-044：取数参数可引用选中行，通道一变自动重取；也可被显式刷新。
            "table.datasource.selection", "swimlane.datasource.selection",
            // REQ-UI-045：控制面板的文本框/轮换选项框把当前值发布到选择通道。
            "panel.channel",
            // REQ-UI-056：弹出层可以接到页面右键上，从而连按钮都不占版面。
            "popup.trigger",
        };



    /// <summary>
    /// 取数参数里的通道引用。只认 <c>selection.</c> 打头的那一种——
    /// 取数没有"面板控件"这个作用域，别的花括号原样留给指令自己解释。
    /// </summary>
    [GeneratedRegex(@"\{(selection\.[^{}\s]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex SelectionPlaceholder();

    public static RenderedPage Render(PageDescription page, PageRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        var state = new RenderState(context, page.Id);
        var root = page.Content == null
            ? Placeholder("content", state)
            : Build(page.Content, state);

        root = AttachContextPopup(root, state);

        return new RenderedPage
        {
            Root = root,
            MissingComponents = state.Missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>
    /// 把 <c>trigger: "context"</c> 的弹出层接到整页的右键上（REQ-UI-056）。
    ///
    /// **必须在整棵树建完之后做**：弹出层节点可以写在页面的任何位置，而它要接的是
    /// 「这一页」，不是它自己那一格。
    ///
    /// **必须自己包一层透明底的容器**：<see cref="Panel"/> 的 Background 为 null 时
    /// 空白处不参与命中测试，而空白处正是这条路唯一会被右键到的地方——行上是行菜单，
    /// 表格自己也有底色。直接往 root 上写底色则不行：root 可能就是某个组件本体
    /// （只有一个 table 的页面），覆盖它的底色等于从外面改组件的外观。
    ///
    /// **浮层本体挂在这一层，不留在它声明的那一格**。两个理由：
    /// 声明的位置对右键式浮层没有意义（它接的是整页），而留在原位就得让那一格
    /// 既不占空间又保持可见——WPF 的 Popup 在 Collapsed 的父级下**开不出来**，
    /// 且不抛异常、不触发 Closed，IsOpen 写进去就是 false。
    /// </summary>
    private static FrameworkElement AttachContextPopup(FrameworkElement root, RenderState state)
    {
        if (state.ContextPopup is not { } flyout)
            return root;

        var surface = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        surface.Children.Add(root);
        surface.Children.Add(flyout);
        flyout.AttachContextTrigger(surface);
        return surface;
    }

    /// <summary>
    /// 会把剩余空间吃掉的节点。纵向栈里它们拿星号行，其余按内容高度。
    ///
    /// 判据是"它自己带滚动"：表格与泳道图都有内部视口，**必须**被限住高度才谈得上滚动。
    /// 放在 StackPanel 里的话它们量到的是无穷高，于是一次性把全部行画出来——
    /// 表现就是"表格撑满整页、还滚不动"（Janus 实测）。
    /// </summary>
    private static bool IsGreedy(PageNode node)
    {
        var type = (node.Type ?? "").ToLowerInvariant();
        if (type is "table" or "swimlane")
            return true;

        // 容器跟着里面走：栅格里放了表格，那一格照样得拿到高度。
        // switch 同理，而且**必须**算上：它的分支里放着表格，少算的话
        // 切过去看到的是一张只有表头、按内容高度缩成一条的表。
        return type is "stack" or "grid" or "switch" && (node.Children ?? []).Any(IsGreedy);
    }

    private static FrameworkElement Build(PageNode node, RenderState state)
    {
        var type = (node.Type ?? "").ToLowerInvariant();
        if (RetiredComponents.TryGetValue(type, out var label))
            return Retired(type, label, state);

        if (node.Channel is { Length: > 0 } && type != "table")
            state.WarnUnbound($"只有表格能声明选择通道，{type} 上的 channel={node.Channel} 已忽略");

        return type switch
        {
            "stack" => BuildStack(node, state),
            "text" => BuildText(node),
            "table" => BuildTable(node, state),
            "panel" => BuildPanel(node, state),
            "swimlane" => BuildSwimlane(node, state),
            "grid" => BuildGrid(node, state),
            "popup" => BuildPopup(node, state),
            "switch" => BuildSwitch(node, state),
            _ => Placeholder(node.Type, state),
        };
    }

    /// <summary>
    /// 顺序容器。**用 Grid 而不是 StackPanel**：StackPanel 在排列方向上给子元素无穷尺寸，
    /// 而表格与泳道图靠"被限住尺寸"才滚得起来——放进 StackPanel 的表格会把每一行都画出来，
    /// 撑满整页且没有滚动条（Janus 项目总览页实测）。
    ///
    /// 因此按 <see cref="IsGreedy"/> 分两档：会吃空间的拿星号，其余按内容尺寸。
    /// 一个都不吃时全是 Auto，行为与原来的 StackPanel 一致。
    /// </summary>
    private static FrameworkElement BuildStack(PageNode node, RenderState state)
    {
        var horizontal = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        var grid = new Grid();

        var gap = (node.Gap ?? "").ToLowerInvariant() switch
        {
            "none" => 0d,
            "tight" => GapTight,
            _ => GapNormal,
        };

        var children = node.Children ?? [];
        for (var i = 0; i < children.Count; i++)
        {
            var length = IsGreedy(children[i])
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;

            if (horizontal)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
            else
                grid.RowDefinitions.Add(new RowDefinition { Height = length });

            var child = Build(children[i], state);
            if (gap > 0 && i < children.Count - 1)
                child.Margin = horizontal
                    ? new Thickness(0, 0, gap, 0)
                    : new Thickness(0, 0, 0, gap);

            if (horizontal)
                Grid.SetColumn(child, i);
            else
                Grid.SetRow(child, i);

            grid.Children.Add(child);
        }

        return grid;
    }

    private static FrameworkElement BuildText(PageNode node)
    {
        var text = new TextBlock { Text = node.Text ?? "", TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(FrameworkElement.StyleProperty, (node.Style ?? "").ToLowerInvariant() switch
        {
            "caption" => "Aurora.Text.Caption",
            "secondary" => "Aurora.Text.Secondary",
            _ => "Aurora.Text.Body",
        });
        return text;
    }

    /// <summary>
    /// 表格：本类只把列声明翻译成组件输入，一个控件都不拼。
    /// 行高、字号、颜色、圆角、分隔线全归 <see cref="AuroraTable"/>，描述里传不进来。
    /// </summary>
    private static FrameworkElement BuildTable(PageNode node, RenderState state)
    {
        var broken = new List<string>();
        var columns = (node.Columns ?? [])
            .Select(column =>
            {
                if (column.CellAction == null)
                    return new AuroraTableColumn(column.Key, column.Title, column.Width);
                if (string.IsNullOrWhiteSpace(column.CellAction))
                {
                    broken.Add($"列 {column.Title}：cellAction 为空");
                    return new AuroraTableColumn(column.Key, column.Title, column.Width);
                }

                var binding = state.ResolveAction(column.CellAction);
                if (!binding.Ok)
                {
                    broken.Add($"列 {column.Title}：{binding.Error}");
                    return new AuroraTableColumn(column.Key, column.Title, column.Width);
                }

                var action = binding.Action!;
                return new AuroraTableColumn(
                    column.Key,
                    column.Title,
                    column.Width,
                    new AuroraCellAction(
                        column.CellAction,
                        string.IsNullOrWhiteSpace(action.Summary) ? action.Title : action.Summary,
                        action.Danger));
            })
            .ToList();

        var table = new AuroraTable { EmptyText = "暂无数据" };
        table.SetData(AuroraTableData.Create(columns, []));

        if (node.Id is { Length: > 0 } id)
        {
            state.RegisterNode(id, table);

            // 列序按「模块/页面/节点」记（REQ-UI-062）。没写 id 的表不记：
            // 那样的表拖完也认不出是哪一张，记下来只会张冠李戴。
            table.UseColumnOrder(state.ColumnOrder, state.ColumnOrderKey(id));
        }

        if (node.Channel is { Length: > 0 } channel)
            state.PublishSelectionTo(table, channel, node.Id);

        if (node.DataSource is { } source && !string.IsNullOrWhiteSpace(source.Command))
            state.BindRows(table, columns, source, node.Id);

        if (columns.Any(column => column.CellAction != null))
            state.BindCellActions(table);

        var declared = node.RowActions ?? [];
        var bound = new List<AuroraRowAction>();
        foreach (var action in declared)
        {
            if (action.Action is not { Length: > 0 } actionId)
            {
                broken.Add((action.Title.Length > 0 ? action.Title : "(无标题)") + "：未声明 action");
                continue;
            }

            var binding = state.ResolveAction(actionId);
            if (!binding.Ok)
            {
                broken.Add(binding.Error!);
                continue;
            }

            state.BindRowAction(table, actionId, action);
            bound.Add(new AuroraRowAction(
                actionId,
                action.Title.Length > 0 ? action.Title : binding.Action!.Title,
                string.Equals(action.Style, "danger", StringComparison.OrdinalIgnoreCase)
                    || binding.Action!.Danger,
                action.Inline,
                binding.Action!.Summary));
        }

        if (declared.Count > 0)
            table.SetRowActions(bound);
        if (broken.Count == 0)
            return table;

        // 断链的行/单元格操作不能只是"少了一个按钮"——那是看不出来的。
        // 表照画，上面加一块写清是哪几条断了。
        foreach (var reason in broken)
            state.WarnUnbound("表格操作未绑定: " + reason);

        var stack = new StackPanel();
        var notice = Box("表格操作未绑定：" + string.Join("；", broken));
        notice.Margin = new Thickness(0, 0, 0, GapTight);
        stack.Children.Add(notice);
        stack.Children.Add(table);
        return stack;
    }

    /// <summary>
    /// 切换容器（REQ-UI-046）：同一块版面上按一个通道值轮换显示其中一支。
    ///
    /// **它换的是组件，不是页面。** 三块内容各自还是普通的 stack / table / panel，
    /// 只是同一时刻只有一支挂在树上。这样才谈得上"三个页签收进一页"——
    /// 收成三个页签是停靠层的事，收成一个控件是这里的事。
    ///
    /// 三条实现上的硬要求：
    /// <list type="bullet">
    ///   <item><b>全部分支在渲染时就建出来</b>。缺件、断链和通道声明都记在
    ///         <see cref="RenderState"/> 上，而它在 <see cref="Render"/> 返回时就被快照走了；
    ///         懒建的分支会让"这一支里有个缺件"永远不出账——正是本协议要消灭的静默；</item>
    ///   <item><b>不显示的分支不挂在树上</b>，而不是 <c>Collapsed</c>。折叠元素照样收 Loaded，
    ///         于是三支的取数会在开页那一刻一起打出去；Janus 的落地状态那一支每次跑两条
    ///         <c>git ls-files</c>，没人看的两支不该付这个钱；</item>
    ///   <item><b>切走再切回来用的是同一个控件实例</b>。重建的话，表格的滚动位置、
    ///         筛选词和选中行会在每次切换时消失，而那些正是人切走之前留下的上下文。</item>
    /// </list>
    /// </summary>
    private static FrameworkElement BuildSwitch(PageNode node, RenderState state)
    {
        var children = node.Children ?? [];
        if (children.Count == 0)
            return Unbound("切换容器没有任何分支：switch 至少要声明一个 children", state);

        var reference = Unwrap(node.Source);
        if (!SelectionChannels.TrySplitReference(reference, out var channel, out var column))
            return Unbound(
                "切换容器的 source 必须写成 {selection.<通道>.<列>}: "
                + (node.Source ?? "(未声明)"),
                state);

        // 重复的 case 只有第一支会被选中，另一支从此永远不显示——
        // 而"某一支怎么点都出不来"是查不出来的，必须在建页时说出来。
        foreach (var duplicate in children
                     .Select(child => child.Case)
                     .Where(name => name is { Length: > 0 })
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            state.WarnUnbound($"切换容器的分支 case 重复: {duplicate.Key}，只有第一支会被显示");

        for (var index = 1; index < children.Count; index++)
            if (string.IsNullOrWhiteSpace(children[index].Case))
                state.WarnUnbound($"切换容器的第 {index + 1} 支没有 case，它永远不会被选中");

        var host = new Grid();
        var branches = children.Select(child => Build(child, state)).ToList();
        var shown = -1;

        void Apply()
        {
            var index = Match(children, state.Channels?.Value(channel, column));
            if (index == shown)
                return;

            // 先摘再挂：WPF 的元素只能有一个父。摘下来的那支仍被 branches 持有，
            // 因此它里面的数据、滚动位置和选中行都还在。
            host.Children.Clear();
            host.Children.Add(branches[index]);
            shown = index;
        }

        Apply();

        // 订阅不解除，与 PanelView 同一处理：页面撤销由 ModulePageLoader 走 owner 维度，
        // 控件这一级没有"页没了"的可靠信号——Unloaded 在切页签、浮出、自动隐藏时都会来。
        if (state.Channels is { } channels)
            channels.Changed += (_, e) =>
            {
                if (string.Equals(e.Channel, channel, StringComparison.OrdinalIgnoreCase))
                    Apply();
            };

        // 发布方可能排在本节点之后建（面板写在 switch 下面），那样初值早于订阅发出。
        // Loaded 时再对一次表，Apply 本身按当前支早退，重复调用不产生额外代价。
        host.Loaded += (_, _) => Apply();
        return host;
    }

    /// <summary>哪一支匹配当前值；没有任何一支匹配（含通道还没有值）时用第一支。</summary>
    private static int Match(IReadOnlyList<PageNode> children, string? value)
    {
        if (value is not { Length: > 0 })
            return 0;

        for (var index = 0; index < children.Count; index++)
            if (string.Equals(children[index].Case, value, StringComparison.OrdinalIgnoreCase))
                return index;

        return 0;
    }

    /// <summary>
    /// 去掉 <c>source</c> 外面那对花括号。取数参数里的通道引用是嵌在字符串里的，
    /// 因而带括号；这里整个字段就是一条引用，两种写法都收下，省得人按写法猜。
    /// </summary>
    private static string? Unwrap(string? reference)
    {
        var text = reference?.Trim();
        return text is { Length: > 1 } && text[0] == '{' && text[^1] == '}'
            ? text[1..^1].Trim()
            : text;
    }

    /// <summary>响应式栅格：只声明"一列至少多宽"，列数由可用宽度算（REQ-UI-015）。</summary>
    private static FrameworkElement BuildGrid(PageNode node, RenderState state)
    {
        var grid = new AuroraGridPanel
        {
            Gap = (node.Gap ?? "").ToLowerInvariant() switch
            {
                "none" => 0d,
                "tight" => GapTight,
                _ => GapNormal,
            },
        };

        if (node.Min is { } min && min > 0)
            grid.MinColumnWidth = min;

        foreach (var child in node.Children ?? [])
        {
            var element = Build(child, state);
            element.SetValue(AuroraGridPanel.FillHeightProperty, IsGreedy(child));
            grid.Children.Add(element);
        }

        return grid;
    }

    /// <summary>
    /// 弹出层：内容仍是面板那三种小组件，只是不再常驻占版面（REQ-UI-012）。
    ///
    /// <c>trigger</c> 决定怎么打开它（REQ-UI-056）：缺省自带一个按钮，
    /// <c>context</c> 则连按钮都不占版面，改由整页的右键唤起。浮层本体是同一个。
    /// </summary>
    private static FrameworkElement BuildPopup(PageNode node, RenderState state)
    {
        if (state.Actions == null)
            return Unbound("弹出层里的按钮需要动作声明台账，当前不可用", state);

        var definition = new PanelDefinition
        {
            Id = node.Id ?? "page-popup",
            Title = node.Text ?? "更多",
            Rows = (node.Rows ?? []).ToList(),
        };

        var parsed = PanelDefinitionValidator.Validate(definition);
        if (!parsed.Ok)
            return Unbound(parsed.Error!, state);

        var trigger = (node.Trigger ?? "").Trim().ToLowerInvariant();
        var context = trigger == "context";
        if (trigger.Length > 0 && !context && trigger != "button")
            // 静默按缺省处理的症状是「右键怎么点都没反应」，而版面上确实多了个按钮，
            // 看上去就像这个组件本来就长这样。
            state.WarnUnbound(
                $"弹出层 {definition.Id} 的 trigger={node.Trigger} 无法识别，按 button 处理；只支持 button / context");

        var flyout = new AuroraFlyout(
            node.Text ?? "更多",
            new PanelView(parsed.Value!, state.Bus, state.Log, state.Actions, state.Channels, state.Owner, state.Refresher, state.PageId),
            string.Equals(node.Style, "accent", StringComparison.OrdinalIgnoreCase),
            context);

        if (!context)
            return flyout;

        if (!state.TryTakeContextPopup(flyout))
            return Unbound(
                $"弹出层 {definition.Id} 声明了 trigger=context，但本页已经接了一个；"
                + "右键只有一次，第二个不会被接上", state);

        // 浮层本体由 AttachContextPopup 挂到整页那一层去，这一格只留一个不占版面的空位：
        // 顺序容器给每个子节点分一行并补一段间距，零尺寸的元素照样会留下那段间距，
        // 而 Collapsed 的元素连边距都不参与。
        return new Grid { Visibility = Visibility.Collapsed };
    }

    /// <summary>面板：直接复用控制面板那套组件与校验，不为页面另造一份。</summary>
    private static FrameworkElement BuildPanel(PageNode node, RenderState state)
    {
        if (state.Actions == null)
            return Unbound("面板按钮需要动作声明台账，当前不可用", state);

        var definition = new PanelDefinition
        {
            Id = node.Id ?? "page-panel",
            Title = node.Text ?? "面板",
            // 行必须原样传下去（REQ-UI-060）。漏传的症状不是报错而是"版面变了"：
            // 面板照样画出来，只是所有元素挤成一行或散成一列。
            Rows = (node.Rows ?? []).ToList(),
        };

        var parsed = PanelDefinitionValidator.Validate(definition);
        if (!parsed.Ok)
            return Unbound(parsed.Error!, state);

        return new PanelView(parsed.Value!, state.Bus, state.Log, state.Actions, state.Channels, state.Owner, state.Refresher, state.PageId);
    }

    /// <summary>泳道图：描述由取数命令给，布局与绘制归组件（REQ-UI-010）。</summary>
    private static FrameworkElement BuildSwimlane(PageNode node, RenderState state)
    {
        if (state.Actions == null)
            return Unbound("泳道图的节点点击需要动作声明台账，当前不可用", state);

        var swimlane = new AuroraSwimlane(state.Bus, state.Log, state.Actions);
        if (node.DataSource is { } source && !string.IsNullOrWhiteSpace(source.Command))
            state.BindSwimlane(swimlane, source, node.Id);
        else
            swimlane.ShowMessage("未声明取数命令");
        return swimlane;
    }

    /// <summary>
    /// 缺件占位。做成看得见的一块而不是空白：缺件必须能被发现，
    /// 而"这一页少了点东西"用肉眼是看不出来的。
    /// </summary>
    private static FrameworkElement Placeholder(string? type, RenderState state)
    {
        var name = string.IsNullOrWhiteSpace(type) ? "(未命名)" : type;
        state.Missing.Add(name);
        return Box("该组件待交付：" + name);
    }

    /// <summary>
    /// 退役类型的样子。与缺件区分开：这不是"还没做"，是"决定了不放在这一层"，
    /// 因此不记进缺件表，也就不会变成一条组件申请。
    /// </summary>
    private static FrameworkElement Retired(string type, string label, RenderState state)
    {
        var reason = $"{label}（{type}）不再是页面节点：小型交互控件请放进控制面板（panel / popup）";
        state.WarnUnbound(reason);
        return Box(reason);
    }

    /// <summary>动作没落点时的样子。与缺件区分开：组件是有的，缺的是声明。</summary>
    private static FrameworkElement Unbound(string reason, RenderState state)
    {
        state.WarnUnbound(reason);
        return Box(reason);
    }

    private static FrameworkElement Box(string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Text.Caption");

        var border = new Border { Child = text, Padding = new Thickness(GapTight) };
        border.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Panel.Unbound");
        return border;
    }

    /// <summary>一次渲染的可变状态：节点表、缺件表与待连线的启用条件。</summary>
    private sealed class RenderState(PageRenderContext context, string pageId)
    {
        private readonly Dictionary<string, AuroraTable> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AuroraTable, Dictionary<string, PageRowAction>> _rowActions = [];
        private readonly HashSet<AuroraTable> _cellActionTables = [];

        public List<string> Missing { get; } = [];

        /// <summary>
        /// 本页接到右键上的那个弹出层（REQ-UI-056）。**只留一个**：右键只有一次，
        /// 留两个的话「弹出哪一个」就取决于建页顺序，而那个顺序不受任何东西保证——
        /// 与选择通道的抢注规则同一条理由。
        /// </summary>
        public AuroraFlyout? ContextPopup { get; private set; }

        public bool TryTakeContextPopup(AuroraFlyout flyout)
        {
            if (ContextPopup != null)
                return false;

            ContextPopup = flyout;
            return true;
        }

        public CommandBus Bus => context.Bus;

        public IShellLog Log => context.Log;

        public ActionRegistry? Actions => context.Actions;

        public SelectionChannels? Channels => context.Channels;

        public PageDataRefresher? Refresher => context.Refresher;

        public string PageId => pageId;

        /// <summary>提供方模块名。面板声明的通道按它登记，页面撤销时随 owner 一并撤掉。</summary>
        public string Owner => context.Owner;


        public void RegisterNode(string id, AuroraTable table) => _nodes[id] = table;

        /// <summary>列序记忆的落点。</summary>
        public IColumnOrderStore? ColumnOrder => context.ColumnOrder;

        /// <summary>
        /// 一张表在列序台账里的身份：<c>模块/页面/节点</c>。
        /// 与通道来源用的是同一套三段式——同一张表在两本账里应该叫同一个名字。
        /// </summary>
        public string ColumnOrderKey(string nodeId) => $"{context.Owner}/{pageId}/{nodeId}";

        /// <summary>
        /// 把一张表接到选择通道上。**来源写成 owner/页/节点**：热重载时同一个节点重新渲染，
        /// 来源不变，因此不会被判成"两张表抢同一个通道"。
        /// </summary>
        public void PublishSelectionTo(AuroraTable table, string channel, string? nodeId)
        {
            if (context.Channels is not { } channels)
            {
                WarnUnbound($"选择通道台账不可用，表格声明的 channel={channel} 已忽略");
                return;
            }

            var origin = $"{context.Owner}/{pageId}#{nodeId ?? "(无 id)"}";
            if (!channels.TryDeclare(channel, context.Owner, origin, out var error))
            {
                WarnUnbound(error);
                return;
            }

            table.SelectionChanged += (_, _) => channels.Publish(channel, table.SelectedRow);

            // 换数据时 ListView 会清掉选中，SelectionChanged 随之把通道置空——
            // 于是"刷新后按钮还亮着、点下去用的却是上一份数据里的行"这种事不成立。
            channels.Publish(channel, table.SelectedRow);
        }

        /// <summary>
        /// 记住某张表的某条行操作声明。每张表只挂一次事件：
        /// 行操作的分派按 id 走，挂多次的话一次点击会发出多条命令。
        /// </summary>
        public void BindRowAction(AuroraTable table, string id, PageRowAction action)
        {
            if (!_rowActions.TryGetValue(table, out var map))
            {
                map = new Dictionary<string, PageRowAction>(StringComparer.Ordinal);
                _rowActions[table] = map;
                table.RowActionInvoked += (_, e) => InvokeRowAction(table, e);
            }

            map[id] = action;
        }

        private async Task<bool> ExecuteActionAsync(string text)
            => (await Task.Run(() => context.Bus.ExecuteAsync(text, "UI"))).Success;

        private void InvokeRowAction(AuroraTable table, AuroraRowActionEventArgs e)
        {
            e.Completion = Task.FromResult(false);
            if (!_rowActions.TryGetValue(table, out var map)
                || !map.TryGetValue(e.Action.Id, out var declared))
                return;

            // 点击时**现取**声明：构建时固化下来的那份在模块热重载后就是过期的。
            var binding = ResolveAction(e.Action.Id);
            if (!binding.Ok)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + binding.Error);
                return;
            }

            var text = ActionRegistry.BuildCommandText(
                binding.Action!,
                SelectionChannels.Chain(
                    context.Channels,
                    name => ResolveRowArgument(declared, e.Row, name)),
                out var error);
            if (text == null)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + error);
                return;
            }

            e.Completion = ExecuteActionAsync(text);
        }

        /// <summary>把表格的单元格动作接到动作台账；同一张表只挂一次事件。</summary>
        public void BindCellActions(AuroraTable table)
        {
            if (_cellActionTables.Add(table))
                table.CellActionInvoked += (_, e) => InvokeCellAction(e);
        }

        private void InvokeCellAction(AuroraCellActionEventArgs e)
        {
            e.Completion = Task.FromResult(false);
            // 与行操作一致，点击时现取声明，避免模块热重载后继续执行旧指令。
            var binding = ResolveAction(e.Action.Id);
            if (!binding.Ok)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + binding.Error);
                return;
            }

            var text = ActionRegistry.BuildCommandText(
                binding.Action!,
                SelectionChannels.Chain(
                    context.Channels,
                    name => e.Row.TryGetValue(name, out var cell) ? cell : null),
                out var error);
            if (text == null)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + error);
                return;
            }

            e.Completion = ExecuteActionAsync(text);
        }

        /// <summary>
        /// 行操作的占位符默认取**被点那一行**的同名列——行操作的作用域天然就是一行。
        /// 显式写了 args 的以 args 为准（写死值，或跨节点取值）。
        /// </summary>
        private string? ResolveRowArgument(
            PageRowAction action,
            IReadOnlyDictionary<string, string> row,
            string name)
        {
            if (action.Args != null && action.Args.TryGetValue(name, out var argument))
                return Resolve(argument);
            return row.TryGetValue(name, out var cell) ? cell : null;
        }

        public ActionBinding ResolveAction(string id)
            => context.Actions?.Resolve(id)
               ?? ActionBinding.Fail($"未声明的动作: {id}（动作声明台账不可用）");

        public void WarnUnbound(string reason)
            => context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + reason);

        /// <summary>
        /// 直接写指令名的按钮不拦，但要留痕：模块改一次指令名它就会静默失效，
        /// 而"哪些按钮还没换成动作"必须是可查的，不能只存在于某次代码走查的印象里。
        /// </summary>
        public void WarnRawCommand(string command)
            => context.Log.Log(
                ShellLogLevel.Warn,
                "page",
                context.Owner + ": 按钮直接绑定指令 " + command
                + "，改名后会静默失效；建议改用模块声明的动作（<域>.ui.actions）");

        /// <summary>组件调用走指令总线：按钮点击变成一条命令，参数可从视图状态取值。</summary>
        public void Invoke(PageInvoke invoke)
        {
            string text;
            if (invoke.Action is { Length: > 0 } actionId)
            {
                // 点击时**现取**声明：构建时固化下来的那份在模块热重载后就是过期的。
                var binding = ResolveAction(actionId);
                if (!binding.Ok)
                {
                    context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + binding.Error);
                    return;
                }

                var built = ActionRegistry.BuildCommandText(
                    binding.Action!,
                    control => ResolveArgument(invoke, control),
                    out var error);
                if (built == null)
                {
                    context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + error);
                    return;
                }

                text = built;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(invoke.Command))
                    return;

                text = invoke.Command;
                foreach (var pair in invoke.Args ?? new Dictionary<string, PageArgument>())
                {
                    var value = Resolve(pair.Value);
                    if (value == null)
                        continue;
                    text += " " + pair.Key + "=" + CommandParser.QuoteArg(value);
                }
            }

            _ = context.Bus.ExecuteAsync(text, "UI");
        }

        /// <summary>动作占位符 <c>{name}</c> 从本按钮的 args 取值，取不到返回 null 让动作报错。</summary>
        private string? ResolveArgument(PageInvoke invoke, string name)
            => invoke.Args != null && invoke.Args.TryGetValue(name, out var argument)
                ? Resolve(argument)
                : null;

        private string? Resolve(PageArgument argument)
        {
            if (argument.Value is { } literal)
                return literal;
            if (argument.From is not { Length: > 0 } path)
                return null;

            // 形如 commands.selected.name：取 id 为 commands 的节点当前选中行的 name 列。
            var parts = path.Split('.');
            if (parts.Length != 3 || !string.Equals(parts[1], "selected", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!_nodes.TryGetValue(parts[0], out var table))
                return null;
            if (table.SelectedRow is not { } row)
                return null;
            return row.TryGetValue(parts[2], out var cell) ? cell : null;
        }

        /// <summary>
        /// 取数：调模块的只读命令，把返回的 JSON 行灌进表格。
        ///
        /// 优先读 <c>Data</c>；跨进程中继后结构化载荷不会原样存活，
        /// 因此协议要求同样的 JSON 也出现在 <c>Message</c> 里，这里回退到它。
        /// </summary>
        public void BindRows(
            AuroraTable table,
            IReadOnlyList<AuroraTableColumn> columns,
            PageDataSource source,
            string? nodeId)
        {
            Task? active = null;
            var requested = 0;
            string? previousText = null;
            string? revision = null;
            Task Reload()
            {
                requested++;
                return active is { IsCompleted: false } ? active : active = ReloadCore();
            }
            async Task ReloadCore()
            {
                int generation;
                do
                {
                    generation = requested;
                    var text = Compose(source, out var reason);
                    if (text == null)
                    {
                        table.EmptyText = reason;
                        table.SetData(AuroraTableData.Create(columns, []));
                        table.ShowStatus(reason);
                        previousText = null;
                        revision = null;
                        continue;
                    }
                    var sameQuery = text == previousText;
                    var delta = sameQuery && revision != null && !string.IsNullOrWhiteSpace(source.DeltaCommand);
                    var command = delta
                        ? source.DeltaCommand + text[source.Command.Length..] + " since=" + CommandParser.QuoteArg(revision!)
                        : text;
                    table.ShowStatus(sameQuery ? "正在刷新" : "正在加载（显示上次内容）");
                    try
                    {
                        var result = await Task.Run(async () =>
                        {
                            var payload = await FetchAsync(command).ConfigureAwait(false);
                            if (payload == null)
                                throw new InvalidOperationException("取数失败，详见日志");
                            return TableUpdate.Read(payload);
                        }).ConfigureAwait(true);
                        // 合并刷新风暴；旧查询的结果永远不落到新选择上。
                        if (generation != requested || text != Compose(source, out _))
                            continue;
                        if (result.IsDelta)
                        {
                            if (!delta || string.IsNullOrWhiteSpace(source.RowKey))
                                throw new InvalidOperationException("增量结果需要完整快照、rowKey 和 deltaCommand");
                            table.ApplyDelta(source.RowKey, result.Rows, result.Removes);
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(source.RowKey))
                                result.ValidateKeys(source.RowKey);
                            table.SetSnapshot(AuroraTableData.Create(columns, result.Rows), source.RowKey);
                        }
                        table.EmptyText = "暂无数据";
                        previousText = text;
                        revision = result.Revision;
                    }
                    catch (Exception ex)
                    {
                        if (generation == requested)
                            table.ShowStatus("刷新失败，保留原内容：" + ex.Message);
                    }
                } while (generation != requested);

            }
            Bind(table, source, nodeId, Reload);
        }

        public void BindSwimlane(AuroraSwimlane swimlane, PageDataSource source, string? nodeId)
            => Bind(swimlane, source, nodeId, () => LoadSwimlaneAsync(swimlane, source));

        /// <summary>
        /// 取数绑定：进树时取一次，此后由通道变化或显式刷新再取。
        ///
        /// **不能只在 Loaded 取一次**。取数参数可以引用选中行，而选中会变；
        /// 只取一次的表在换选中之后显示的是上一个项目的数据，
        /// 而"过期的数据"和"新数据"在界面上长得一模一样。
        /// </summary>
        private void Bind(
            FrameworkElement element,
            PageDataSource source,
            string? nodeId,
            Func<Task> reload)
        {
            Task DispatchReload() => element.Dispatcher.InvokeAsync(reload).Task.Unwrap();
            WhenLoaded(element, DispatchReload);
            context.Refresher?.Register(context.Owner, pageId, nodeId, ChannelsOf(source), DispatchReload);
        }

        private static void WhenLoaded(FrameworkElement element, Func<Task> load)
        {
            var started = false;
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                if (started)
                    return;

                started = true;
                element.Loaded -= handler;
                element.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => _ = load()));
            };
            element.Loaded += handler;
        }

        /// <summary>
        /// 组装取数指令。参数值里的 <c>{selection.&lt;通道&gt;.&lt;列&gt;}</c> 用当前选中行替换。
        ///
        /// 取不到值时**返回 null 而不是发一条参数为空的指令**：没选中项目就去问
        /// 「这个项目的历史」，拿回来的要么是错误要么是别人的历史，两种都比空表糟。
        /// </summary>
        private string? Compose(PageDataSource source, out string reason)
        {
            reason = "";
            var text = source.Command;
            var unresolved = new List<string>();

            foreach (var pair in source.Args ?? new Dictionary<string, string>())
            {
                var value = SelectionPlaceholder().Replace(pair.Value ?? "", match =>
                {
                    var name = match.Groups[1].Value;

                    // **判 null，不判空**。通道里有没有行，与那一行的某一格是不是空串，
                    // 是两件事：没有行 → 取不到值，照旧拒绝取数并说清等谁；
                    // 有行但那一格是空的 → 那就是个空值，照常取数。
                    //
                    // 混为一谈的后果是：控制面板的文本框在页面打开时就会把自己的初值
                    // 发上通道（REQ-UI-045），而初值通常是空串——于是一个用作**筛选**的
                    // 搜索框会让整张表停在「请先选中一行」，而那一格根本不是让人选行的。
                    // 1.9.0 命令集改成描述式时当场撞上（CommandPagesContractTests 有专条）。
                    //
                    // 动作那一侧（ActionRegistry.BuildCommandText）本来就是判 null 的，
                    // 因此这里也是把两处口径对齐。
                    var resolved = context.Channels?.Resolve(name);
                    if (resolved != null)
                        return resolved;
                    unresolved.Add(name);
                    return match.Value;
                });

                text += " " + pair.Key + "=" + CommandParser.QuoteArg(value);
            }

            if (unresolved.Count == 0)
                return text;

            reason = "请先选中一行（取数需要 " + string.Join("、", unresolved.Distinct()) + "）";
            return null;
        }

        /// <summary>这条取数引用了哪几个选择通道。空表示它与选中无关。</summary>
        private static IReadOnlyList<string> ChannelsOf(PageDataSource source)
            => (source.Args ?? new Dictionary<string, string>())
                .Values
                .SelectMany(value => SelectionPlaceholder().Matches(value ?? "")
                    .Select(match => match.Groups[1].Value))
                .Select(reference => SelectionChannels.TrySplitReference(reference, out var channel, out _)
                    ? channel
                    : "")
                .Where(channel => channel.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private async Task LoadSwimlaneAsync(AuroraSwimlane swimlane, PageDataSource source)
        {
            var text = Compose(source, out var reason);
            if (text == null)
            {
                swimlane.ShowMessage(reason);
                return;
            }

            var payload = await Task.Run(() => FetchAsync(text)).ConfigureAwait(true);
            if (payload == null)
            {
                swimlane.ShowMessage("取数失败：" + text);
                return;
            }

            var parsed = SwimlaneReader.Read(payload);
            if (!parsed.Ok)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 泳道描述无效: " + parsed.Error);
                swimlane.ShowMessage("泳道描述无效：" + parsed.Error);
                return;
            }

            swimlane.SetDescription(parsed.Value);
        }

        /// <summary>
        /// 取一次数。走 <c>InvokeAsync</c> 而不是 <c>ExecuteAsync</c>——**取数不是操作**。
        ///
        /// 操作者通道每调一次就往控制台写两行（回显 + 结果）。而一次用户动作会连带
        /// 触发本页全部表格重取：Minerva 属性整备改一格材料，控制台就多出五对
        /// 「minerva.ui.data view=parts」「✓ Minerva 零件」，十行里没有一行是用户想看的，
        /// 真正的结果反倒被顶出屏幕。CommandBus 早就为这类高频内部调用留了安静通道
        /// （见 <c>CommandBus.InvokeAsync</c> 的注释：「问题不在延迟而在语义」）。
        /// 失败仍然照常写日志——下面那两条 Warn 才是取数该发出的声音。
        /// </summary>
        private async Task<string?> FetchAsync(string text)
        {
            CommandResult result;
            try
            {
                // 留一条 Debug 痕迹。安静通道不回显，但「这一页到底取过数没有」在排障时
                // 必须查得到；Debug 在控制台默认级别之下，平时一行都不占。
                context.Log.Log(ShellLogLevel.Debug, "page", context.Owner + ": 取数 " + text);
                result = await context.Bus.InvokeAsync(text, "UI").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数失败 " + text + ": " + ex.Message);
                return null;
            }

            if (!result.Success)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数失败 " + text + ": " + result.Message);
                return null;
            }

            if (!CommandResultData.TryGetJsonText(result.Data, result.Message, out var payload))
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数结果没有合法 JSON 载荷: " + text);
                return null;
            }

            return payload;
        }

    }
}
