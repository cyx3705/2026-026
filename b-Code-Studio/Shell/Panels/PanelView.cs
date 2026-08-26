using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Actions;
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
    private readonly Dictionary<string, Func<string>> _getters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<string>> _setters = new(StringComparer.OrdinalIgnoreCase);
    private PanelDefinition _definition;

    public PanelView(PanelDefinition definition, CommandBus bus, IShellLog log, ActionRegistry actions)
    {
        _bus = bus;
        _log = log;
        _actions = actions;
        _definition = definition;
        // 底色由窗格卡片提供，面板自身不再画一块白（UI 风格规范 §1）。
        Background = Brushes.Transparent;
        AuroraComponentResources.Ensure(this);
        Rebuild(definition);
    }

    public string PanelId => _definition.Id;

    /// <summary>可反向驱动的控件 id 清单（aurora.ui.panelset 报错提示用）。</summary>
    public IReadOnlyList<string> ControlIds => _setters.Keys.ToList();

    /// <summary>按新声明原地重建内容（aurora.ui.panelreload，以及动作声明刷新后重绑按钮）。</summary>
    public void Rebuild(PanelDefinition definition)
    {
        _definition = definition;
        _getters.Clear();
        _setters.Clear();

        var horizontal = string.Equals(definition.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        FrameworkElement content = horizontal
            ? BuildHorizontal(definition)
            : BuildVertical(definition);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = horizontal
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            Content = content,
        };

        var surface = new Border { Child = scroll };
        surface.SetResourceReference(StyleProperty, "Aurora.Panel.Surface");
        Content = surface;
    }

    private Grid BuildVertical(PanelDefinition definition)
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 56 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        for (var index = 0; index < definition.Widgets.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Build(grid, row, definition.Widgets[index]);
            row++;

            if (index >= definition.Widgets.Count - 1)
                continue;

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var divider = new Border();
            divider.SetResourceReference(StyleProperty, "Aurora.Panel.Divider");
            Grid.SetRow(divider, row++);
            Grid.SetColumnSpan(divider, 2);
            grid.Children.Add(divider);
        }

        return grid;
    }

    private StackPanel BuildHorizontal(PanelDefinition definition)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8),
        };

        for (var index = 0; index < definition.Widgets.Count; index++)
        {
            var item = new Grid { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Build(item, 0, definition.Widgets[index]);
            stack.Children.Add(item);

            if (index == definition.Widgets.Count - 1)
                continue;

            var divider = new Border();
            divider.SetResourceReference(StyleProperty, "Aurora.Panel.VerticalDivider");
            stack.Children.Add(divider);
        }

        return stack;
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
        var text = new TextBlock { Text = widget.Text ?? "", Margin = new Thickness(0, 4, 0, 4) };
        text.SetResourceReference(StyleProperty, "Aurora.Panel.Text");
        return text;
    }

    private FrameworkElement BuildTextBox(PanelWidget widget)
    {
        var id = widget.Id!;
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
        return box;
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
        return button;
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
        Grid.SetColumnSpan(element, 2);
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
            control => _getters.TryGetValue(control, out var getter) ? getter() : null,
            out var error);
        if (text == null)
        {
            _log.Error("panel", "面板 " + _definition.Id + ": " + error);
            return;
        }

        _ = _bus.ExecuteAsync(text, "UI");
    }
}
