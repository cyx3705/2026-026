using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Table;
using HistoryAurora.Shell.Themes;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Commands;

namespace HistoryAurora.Shell.Views;

/// <summary>
/// 指令详情（REQ-UI-014）。命令集选中哪条，这里就展开哪条：说明、标志、示例与参数表。
///
/// 与命令集是同一轮找回来的：宿主 5.0 之后这两页一起消失，
/// 而"这条指令收什么参数、必填哪个、允许值有哪些"在控制台以外就无处可查了。
///
/// 联动经 <see cref="CommandSelectionState"/> —— 进程内共享的一个字符串，
/// 只传指令名。传对象的话，两页各自持有的副本会在热重载后不一致。
/// </summary>
internal sealed class CommandDetailView : UserControl
{
    private readonly LocalCommandCatalogSession _catalog;
    private readonly CommandBus _bus;
    private readonly CommandSelectionState _selection;

    private readonly StackPanel _head = new();
    private readonly AuroraTable _parameters = new() { EmptyText = "这条指令没有参数" };
    private readonly TextBlock _empty = new();

    private string? _shown;

    public CommandDetailView(
        LocalCommandCatalogSession catalog,
        CommandBus bus,
        CommandSelectionState selection)
    {
        _catalog = catalog;
        _bus = bus;
        _selection = selection;

        AuroraComponentResources.Ensure(this);
        Background = System.Windows.Media.Brushes.Transparent;

        _empty.SetResourceReference(StyleProperty, "Aurora.Text.Secondary");
        _empty.Text = "在命令集里选一条指令";
        _empty.TextWrapping = TextWrapping.Wrap;

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(_head, Dock.Top);
        root.Children.Add(_head);
        root.Children.Add(new Grid { Children = { _parameters, _empty } });
        Content = root;

        _selection.Changed += OnSelectionChanged;
        Loaded += (_, _) => Refresh();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var name = _selection.CurrentCommandName;
        if (string.Equals(name, _shown, StringComparison.OrdinalIgnoreCase))
            return;
        _shown = name;
        _ = ShowAsync(name);
    }

    private async Task ShowAsync(string? name)
    {
        _head.Children.Clear();
        if (string.IsNullOrWhiteSpace(name))
        {
            _parameters.SetData(null);
            _empty.Text = "在命令集里选一条指令";
            _empty.Visibility = Visibility.Visible;
            return;
        }

        var detail = await _catalog.DetailAsync(name).ConfigureAwait(true);
        if (detail == null)
        {
            _parameters.SetData(null);
            _empty.Text = "查不到这条指令：" + name;
            _empty.Visibility = Visibility.Visible;
            return;
        }

        _empty.Visibility = Visibility.Collapsed;
        BuildHead(detail);

        _parameters.SetData(AuroraTableData.FromItems(
            detail.Parameters,
            ("参数", "120", parameter => parameter.Name),
            ("类型", "64", parameter => parameter.Type),
            ("必填", "44", parameter => parameter.Required ? "是" : ""),
            ("默认", "80", parameter => parameter.Default ?? ""),
            ("说明", AuroraTableColumn.Star, Describe)));
    }

    /// <summary>允许值并进说明列：它是"这个参数能填什么"的一部分，不该另占一列。</summary>
    private static string Describe(CommandParameterInfo parameter)
        => parameter.AllowedValues.Count == 0
            ? parameter.Description
            : parameter.Description + "（可选值：" + string.Join(" / ", parameter.AllowedValues) + "）";

    private void BuildHead(CommandCatalogDetail detail)
    {
        var row = detail.Command;
        _head.Children.Add(Line("Aurora.Detail.Title", row.CommandName));
        _head.Children.Add(Line("Aurora.Detail.Value", row.Summary));

        var flags = new List<string> { row.Domain + " / " + CommandClassLabels.Display(row.CommandClass) };
        if (row.Readonly)
            flags.Add("只读");
        if (row.Dangerous)
            flags.Add("执行前询问");
        if (row.RequiresUiThread)
            flags.Add("界面线程");
        if (row.HiddenReason != null)
            flags.Add("远端隐藏：" + row.HiddenReason);
        flags.Add("来源 " + row.Source);
        _head.Children.Add(Line("Aurora.Detail.Key", string.Join(" · ", flags)));

        if (!string.IsNullOrWhiteSpace(row.Example))
            _head.Children.Add(Line("Aurora.Detail.Mono", "示例  " + row.Example));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 8),
        };
        actions.Children.Add(Button("填入控制台", () => Prefill(row.Example ?? row.CommandName)));
        _head.Children.Add(actions);
    }

    private void Prefill(string text)
        => _ = _bus.ExecuteAsync("aurora.log.prefill text=" + CommandParser.QuoteArg(text), "UI");

    private static TextBlock Line(string style, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.SetResourceReference(StyleProperty, "Aurora.Button.Base");
        button.Click += (_, _) => action();
        return button;
    }
}
