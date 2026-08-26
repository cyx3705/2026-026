using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Graph;
using HistoryAurora.Shell.Panels;
using HistoryAurora.Shell.Table;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Pages;

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
public static class PageRenderer
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
            "stack", "text", "table", "panel", "swimlane", "grid", "popup",
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
            "table.rowactions", "menu",
        };

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static RenderedPage Render(PageDescription page, PageRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        var state = new RenderState(context);
        var root = page.Content == null
            ? Placeholder("content", state)
            : Build(page.Content, state);

        return new RenderedPage
        {
            Root = root,
            MissingComponents = state.Missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static FrameworkElement Build(PageNode node, RenderState state)
    {
        var type = (node.Type ?? "").ToLowerInvariant();
        if (RetiredComponents.TryGetValue(type, out var label))
            return Retired(type, label, state);

        return type switch
        {
            "stack" => BuildStack(node, state),
            "text" => BuildText(node),
            "table" => BuildTable(node, state),
            "panel" => BuildPanel(node, state),
            "swimlane" => BuildSwimlane(node, state),
            "grid" => BuildGrid(node, state),
            "popup" => BuildPopup(node, state),
            _ => Placeholder(node.Type, state),
        };
    }

    private static FrameworkElement BuildStack(PageNode node, RenderState state)
    {
        var horizontal = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel
        {
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
        };

        var gap = (node.Gap ?? "").ToLowerInvariant() switch
        {
            "none" => 0d,
            "tight" => GapTight,
            _ => GapNormal,
        };

        var children = node.Children ?? [];
        for (var i = 0; i < children.Count; i++)
        {
            var child = Build(children[i], state);
            if (gap > 0 && i < children.Count - 1)
                child.Margin = horizontal
                    ? new Thickness(0, 0, gap, 0)
                    : new Thickness(0, 0, 0, gap);
            panel.Children.Add(child);
        }

        return panel;
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
        var columns = (node.Columns ?? [])
            .Select(column => new AuroraTableColumn(column.Key, column.Title, column.Width))
            .ToList();

        var table = new AuroraTable { EmptyText = "暂无数据" };
        table.SetData(AuroraTableData.Create(columns, []));

        if (node.Id is { Length: > 0 } id)
            state.RegisterNode(id, table);

        if (node.DataSource is { } source && !string.IsNullOrWhiteSpace(source.Command))
            state.LoadRows(table, columns, source);

        var declared = node.RowActions ?? [];
        if (declared.Count == 0)
            return table;

        var bound = new List<AuroraRowAction>();
        var broken = new List<string>();
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

        table.SetRowActions(bound);
        if (broken.Count == 0)
            return table;

        // 断链的行操作不能只是"少了一个按钮"——那是看不出来的。
        // 表照画，上面加一块写清是哪几条断了。
        foreach (var reason in broken)
            state.WarnUnbound("行操作未绑定: " + reason);

        var stack = new StackPanel();
        var notice = Box("行操作未绑定：" + string.Join("；", broken));
        notice.Margin = new Thickness(0, 0, 0, GapTight);
        stack.Children.Add(notice);
        stack.Children.Add(table);
        return stack;
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
            grid.Children.Add(Build(child, state));

        return grid;
    }

    /// <summary>弹出层：内容仍是面板那三种小组件，只是不再常驻占版面（REQ-UI-012）。</summary>
    private static FrameworkElement BuildPopup(PageNode node, RenderState state)
    {
        if (state.Actions == null)
            return Unbound("弹出层里的按钮需要动作声明台账，当前不可用", state);

        var definition = new PanelDefinition
        {
            Id = node.Id ?? "page-popup",
            Title = node.Text ?? "更多",
            Orientation = node.Orientation ?? "vertical",
            Widgets = (node.Widgets ?? []).ToList(),
        };

        var parsed = PanelDefinitionValidator.Validate(definition);
        if (!parsed.Ok)
            return Unbound(parsed.Error!, state);

        return new AuroraFlyout(
            node.Text ?? "更多",
            new PanelView(parsed.Value!, state.Bus, state.Log, state.Actions),
            string.Equals(node.Style, "accent", StringComparison.OrdinalIgnoreCase));
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
            // 声明里的 orientation 必须传下去。漏传的症状不是报错而是"少了点东西"：
            // 面板照样画出来，只是横排变竖排、控件之间那条渐隐分隔线一并消失。
            Orientation = node.Orientation ?? "vertical",
            Widgets = (node.Widgets ?? []).ToList(),
        };

        var parsed = PanelDefinitionValidator.Validate(definition);
        if (!parsed.Ok)
            return Unbound(parsed.Error!, state);

        return new PanelView(parsed.Value!, state.Bus, state.Log, state.Actions);
    }

    /// <summary>泳道图：描述由取数命令给，布局与绘制归组件（REQ-UI-010）。</summary>
    private static FrameworkElement BuildSwimlane(PageNode node, RenderState state)
    {
        if (state.Actions == null)
            return Unbound("泳道图的节点点击需要动作声明台账，当前不可用", state);

        var swimlane = new AuroraSwimlane(state.Bus, state.Log, state.Actions);
        if (node.DataSource is { } source && !string.IsNullOrWhiteSpace(source.Command))
            state.LoadSwimlane(swimlane, source);
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
    private sealed class RenderState(PageRenderContext context)
    {
        private readonly Dictionary<string, AuroraTable> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AuroraTable, Dictionary<string, PageRowAction>> _rowActions = [];

        public List<string> Missing { get; } = [];

        public CommandBus Bus => context.Bus;

        public IShellLog Log => context.Log;

        public ActionRegistry? Actions => context.Actions;


        public void RegisterNode(string id, AuroraTable table) => _nodes[id] = table;

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

        private void InvokeRowAction(AuroraTable table, AuroraRowActionEventArgs e)
        {
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
                name => ResolveRowArgument(declared, e.Row, name),
                out var error);
            if (text == null)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": " + error);
                return;
            }

            _ = context.Bus.ExecuteAsync(text, "UI");
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
        public void LoadRows(
            AuroraTable table,
            IReadOnlyList<AuroraTableColumn> columns,
            PageDataSource source)
            => WhenLoaded(table, () => LoadRowsAsync(table, columns, Compose(source)));

        public void LoadSwimlane(AuroraSwimlane swimlane, PageDataSource source)
            => WhenLoaded(swimlane, () => LoadSwimlaneAsync(swimlane, Compose(source)));

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

        private static string Compose(PageDataSource source)
        {
            var text = source.Command;
            foreach (var pair in source.Args ?? new Dictionary<string, string>())
                text += " " + pair.Key + "=" + CommandParser.QuoteArg(pair.Value);
            return text;
        }

        private async Task LoadRowsAsync(
            AuroraTable table,
            IReadOnlyList<AuroraTableColumn> columns,
            string text)
        {
            // 模块取数可能在第一次 await 前做同步磁盘/Git 工作。放在线程池执行，
            // 避免模块实现细节阻塞 Aurora 的 UI 线程；await 后回 UI 线程更新控件。
            var payload = await Task.Run(() => FetchAsync(text)).ConfigureAwait(true);
            if (payload == null)
                return;

            var rows = ParseRows(payload);
            if (rows == null)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数返回的不是合法行集: " + text);
                return;
            }

            // 列没声明时由数据自己决定行列数（REQ-UI-007）。
            table.SetData(columns.Count > 0
                ? AuroraTableData.Create(columns, rows)
                : AuroraTableData.FromRows(rows));
        }

        private async Task LoadSwimlaneAsync(AuroraSwimlane swimlane, string text)
        {
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

        private async Task<string?> FetchAsync(string text)
        {
            CommandResult result;
            try
            {
                result = await context.Bus.ExecuteAsync(text, "UI").ConfigureAwait(true);
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

        private static List<Dictionary<string, string>>? ParseRows(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return null;
            try
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(payload, RowOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
