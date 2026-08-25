using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Table;
using HistoryAurora.Shell.Themes;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Views;

/// <summary>
/// 命令集（REQ-UI-014）。全部已注册指令的目录：搜、筛、看详情、填进控制台。
///
/// 这一页曾经由 HistoryMercury 的命令工作台挂上来，宿主 5.0 拆掉界面 SDK 之后
/// **整页消失**，连带控制台按 Tab 呼出目录也变成一条报错。它现在由 Aurora 自建：
/// 目录是界面的基础设施，不能挂在某个模块在不在场上。
///
/// 页面自己不画表——表格、行操作、补全输入框都用组件库里的那一份（REQ-UI-007/011/013）。
/// </summary>
internal sealed partial class CommandCatalogView : UserControl
{
    private const string All = "全部";

    private readonly LocalCommandCatalogSession _catalog;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly CommandSelectionState _selection;

    private readonly AuroraTable _table = new() { EmptyText = "没有匹配的指令" };
    private readonly AuroraSuggestBox _search;
    private readonly ComboBox _domains = new();
    private readonly ComboBox _classes = new();
    private readonly TextBlock _status = new();

    private bool _suppressFilterEvents;
    private bool _loaded;

    public CommandCatalogView(
        LocalCommandCatalogSession catalog,
        CommandBus bus,
        IShellLog log,
        CommandSelectionState selection)
    {
        _catalog = catalog;
        _bus = bus;
        _log = log;
        _selection = selection;

        AuroraComponentResources.Ensure(this);
        Background = System.Windows.Media.Brushes.Transparent;

        _search = new AuroraSuggestBox(_catalog.CompleteAsync)
        {
            Placeholder = "搜指令名或说明；Tab 接受候选",
            MinWidth = 240,
        };
        _search.TextChanged += (_, _) => ApplyQuery();

        _domains.SetResourceReference(StyleProperty, "Aurora.Segment.ComboBox");
        _classes.SetResourceReference(StyleProperty, "Aurora.Segment.ComboBox");
        _domains.MinWidth = 110;
        _classes.MinWidth = 110;
        _domains.SelectionChanged += OnDomainChanged;
        _classes.SelectionChanged += OnClassChanged;

        _status.SetResourceReference(StyleProperty, "Aurora.Text.Caption");
        _status.Margin = new Thickness(0, 6, 0, 0);

        ConfigureRowActions();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(_search);
        bar.Children.Add(Spaced(_domains));
        bar.Children.Add(Spaced(_classes));

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_status);
        root.Children.Add(_table);
        Content = root;

        _catalog.Changed += OnCatalogChanged;
        Loaded += async (_, _) =>
        {
            if (_loaded)
                return;
            _loaded = true;
            await _catalog.RefreshAsync().ConfigureAwait(true);
            Rebuild();
        };
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(8, 0, 0, 0);
        return element;
    }

    private void OnCatalogChanged(object? sender, CommandCatalogChangedEventArgs e)
    {
        if (e.Kind == CommandCatalogChangeKind.Selection)
            return;
        Dispatcher.BeginInvoke(async () =>
        {
            if (!IsLoaded)
                return;
            if (e.Kind == CommandCatalogChangeKind.Invalidated)
                await _catalog.RefreshAsync().ConfigureAwait(true);
            Rebuild();
        });
    }

    private void ApplyQuery()
    {
        if (_suppressFilterEvents)
            return;
        _catalog.SetFilter(_catalog.CurrentFilter with { Query = _search.Text });
    }

    private void OnDomainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || _domains.SelectedItem is not string domain)
            return;
        _catalog.TrySetDomain(domain, out _);
    }

    private void OnClassChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || _classes.SelectedItem is not string commandClass)
            return;
        _catalog.TrySetCommandClass(commandClass, out _);
    }

    private void Rebuild()
    {
        var filter = _catalog.CurrentFilter;
        _suppressFilterEvents = true;
        try
        {
            _domains.ItemsSource = new[] { All }.Concat(_catalog.Domains).ToList();
            _domains.SelectedItem = filter.Domain;
            _classes.ItemsSource = new[] { All }.Concat(_catalog.Classes).ToList();
            _classes.SelectedItem = filter.CommandClass;
            // 严格两级：域是「全部」时类不可选（DEC-021）。
            _classes.IsEnabled = filter.Domain != All;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        var rows = _catalog.Visible();
        _table.SetData(AuroraTableData.FromItems(
            rows,
            ("指令", "230", entry => entry.Name),
            ("类", "76", entry => CommandClassLabels.Display(entry.CommandClass)),
            ("只读", "48", entry => entry.Readonly ? "是" : ""),
            ("说明", AuroraTableColumn.Star, entry => entry.Summary)));

        _status.Text = $"{rows.Count} / {_catalog.Entries.Count} 条"
            + (_bus.RemoteExecutor == null ? "（仅界面自持指令，未接宿主目录）" : "");
    }
}
