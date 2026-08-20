using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
/// 两条硬规则：
/// <list type="bullet">
///   <item>描述里不接受任何样式键或颜色，只接受语义档位，映射表写死在本类里；</item>
///   <item>未知组件渲染为**显式占位**并计入缺件清单，绝不静默省略——
///         静默省略正是 <c>DynamicResource</c> 时代的失败形态，改协议就是为了消灭它。</item>
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

        // enabledWhen 依赖节点 id，必须等整棵树建完才能连线。
        state.ApplyDeferredBindings();

        return new RenderedPage
        {
            Root = root,
            MissingComponents = state.Missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static FrameworkElement Build(PageNode node, RenderState state)
        => (node.Type ?? "").ToLowerInvariant() switch
        {
            "stack" => BuildStack(node, state),
            "text" => BuildText(node),
            "button" => BuildButton(node, state),
            "table" => BuildTable(node, state),
            "input" => BuildInput(node),
            "select" => BuildSelect(node),
            _ => Placeholder(node.Type, state),
        };

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

    private static FrameworkElement BuildButton(PageNode node, RenderState state)
    {
        var button = new Button { Content = node.Text ?? "", HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, (node.Style ?? "").ToLowerInvariant() switch
        {
            "accent" => "Aurora.Button.Accent",
            "ghost" => "Aurora.Button.Ghost",
            "danger" => "Aurora.Button.Danger",
            _ => "Aurora.Button.Base",
        });

        if (node.Invoke is { } invoke && !string.IsNullOrWhiteSpace(invoke.Command))
            button.Click += (_, _) => state.Invoke(invoke);

        if (node.EnabledWhen?.Selected is { Length: > 0 } requires)
            state.RequireSelection(button, requires);

        return button;
    }

    private static FrameworkElement BuildTable(PageNode node, RenderState state)
    {
        var view = new GridView();
        foreach (var column in node.Columns ?? [])
        {
            var gvc = new GridViewColumn
            {
                Header = column.Title,
                // 字典行绑定：Binding 的索引器路径对 IDictionary 生效。
                DisplayMemberBinding = new Binding("[" + column.Key + "]"),
            };
            if (column.Width is { Length: > 0 } w && w != "*" && double.TryParse(w, out var px))
                gvc.Width = px;
            view.Columns.Add(gvc);
        }

        var list = new ListView { View = view, SelectionMode = SelectionMode.Single };
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Aurora.Item.Base");

        if (node.Id is { Length: > 0 } id)
            state.RegisterNode(id, list);

        if (node.DataSource is { } source && !string.IsNullOrWhiteSpace(source.Command))
            state.LoadRows(list, source);

        return list;
    }

    private static FrameworkElement BuildInput(PageNode node)
    {
        var box = new TextBox { Text = node.Text ?? "" };
        box.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Segment.TextBox");
        return box;
    }

    private static FrameworkElement BuildSelect(PageNode node)
    {
        var combo = new ComboBox();
        combo.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Segment.ComboBox");
        foreach (var child in node.Children ?? [])
            combo.Items.Add(child.Text ?? "");
        return combo;
    }

    /// <summary>
    /// 缺件占位。做成看得见的一块而不是空白：缺件必须能被发现，
    /// 而"这一页少了点东西"用肉眼是看不出来的。
    /// </summary>
    private static FrameworkElement Placeholder(string? type, RenderState state)
    {
        var name = string.IsNullOrWhiteSpace(type) ? "(未命名)" : type;
        state.Missing.Add(name);

        var text = new TextBlock
        {
            Text = "该组件待交付：" + name,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Text.Caption");

        var border = new Border
        {
            Child = text,
            Padding = new Thickness(GapTight),
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Aurora.Brush.Warning");
        border.SetResourceReference(Border.BackgroundProperty, "Aurora.Brush.SurfaceAlt");
        border.SetResourceReference(Border.CornerRadiusProperty, "Aurora.Radius.Inner");
        return border;
    }

    /// <summary>一次渲染的可变状态：节点表、缺件表与待连线的启用条件。</summary>
    private sealed class RenderState(PageRenderContext context)
    {
        private readonly Dictionary<string, ListView> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(Button Button, string NodeId)> _deferred = [];

        public List<string> Missing { get; } = [];

        public void RegisterNode(string id, ListView list) => _nodes[id] = list;

        public void RequireSelection(Button button, string nodeId) => _deferred.Add((button, nodeId));

        public void ApplyDeferredBindings()
        {
            foreach (var (button, nodeId) in _deferred)
            {
                if (!_nodes.TryGetValue(nodeId, out var list))
                {
                    // 引用了不存在的节点：按"不静默"原则禁用并留日志，而不是当作永远可用。
                    button.IsEnabled = false;
                    context.Log.Log(
                        ShellLogLevel.Warn,
                        "page",
                        context.Owner + ": enabledWhen 引用了不存在的节点 " + nodeId + "，按钮已禁用");
                    continue;
                }

                button.IsEnabled = list.SelectedItem != null;
                list.SelectionChanged += (_, _) => button.IsEnabled = list.SelectedItem != null;
            }
        }

        /// <summary>组件调用走指令总线：按钮点击变成一条命令，参数可从视图状态取值。</summary>
        public void Invoke(PageInvoke invoke)
        {
            var text = invoke.Command;
            foreach (var pair in invoke.Args ?? new Dictionary<string, PageArgument>())
            {
                var value = Resolve(pair.Value);
                if (value == null)
                    continue;
                text += " " + pair.Key + "=" + CommandParser.QuoteArg(value);
            }

            _ = context.Bus.ExecuteAsync(text, "UI");
        }

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
            if (!_nodes.TryGetValue(parts[0], out var list))
                return null;
            if (list.SelectedItem is not IDictionary<string, string> row)
                return null;
            return row.TryGetValue(parts[2], out var cell) ? cell : null;
        }

        /// <summary>
        /// 取数：调模块的只读命令，把返回的 JSON 行灌进表格。
        ///
        /// 优先读 <c>Data</c>；跨进程中继后结构化载荷不会原样存活，
        /// 因此协议要求同样的 JSON 也出现在 <c>Message</c> 里，这里回退到它。
        /// </summary>
        public void LoadRows(ListView list, PageDataSource source)
        {
            var text = source.Command;
            foreach (var pair in source.Args ?? new Dictionary<string, string>())
                text += " " + pair.Key + "=" + CommandParser.QuoteArg(pair.Value);

            _ = LoadAsync(list, text);
        }

        private async Task LoadAsync(ListView list, string text)
        {
            CommandResult result;
            try
            {
                result = await context.Bus.ExecuteAsync(text, "UI").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数失败 " + text + ": " + ex.Message);
                return;
            }

            if (!result.Success)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数失败 " + text + ": " + result.Message);
                return;
            }

            var payload = result.Data as string ?? result.Message;
            var rows = ParseRows(payload);
            if (rows == null)
            {
                context.Log.Log(ShellLogLevel.Warn, "page", context.Owner + ": 取数返回的不是合法行集: " + text);
                return;
            }

            list.ItemsSource = rows;
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
