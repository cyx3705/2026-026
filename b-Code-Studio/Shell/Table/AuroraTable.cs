using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HistoryAurora.Shell.Themes;

namespace HistoryAurora.Shell.Table;

/// <summary>
/// Aurora 表格组件（REQ-UI-007）。**全仓唯一的表格实现**：任何要画表的地方都用它，
/// 不再各自拼 <c>ListView</c> + <c>GridView</c>。
///
/// 分工只有一条线：
/// <list type="bullet">
///   <item><b>宿主给什么</b>：一份行数组，外加可选的列声明（标题、取值键、列宽），
///         以及可选的行操作声明（<see cref="AuroraRowAction"/>）。
///         行数与列数都从数据本身得出，不另传计数。</item>
///   <item><b>组件定什么</b>：颜色、圆角、行高、字号、表头、分隔线、悬停与选中态、
///         空态文案、滚动与省略，以及行操作长成按钮还是菜单。这些**没有对外的入口**——
///         不是"默认值可覆盖"，是根本传不进来。</item>
/// </list>
///
/// 为什么把外观封死：此前每个页面各自组装表格，字号行高各写一遍，改一次主题要逐页对齐，
/// 而漏掉的那一页在浅色下看不出来、深色下才露馅。把外观收进组件后，
/// "只有这一页长得不一样"这种缺陷在结构上就不成立了。
/// </summary>
public sealed class AuroraTable : UserControl
{
    /// <summary>星号列的下限宽度：再窄也要看得见表头文字，否则列会缩成一条缝。</summary>
    private const double MinStarWidth = 48;

    /// <summary>给纵向滚动条留出的余量，避免星号列把内容顶出可视区后又长出横向滚动条。</summary>
    private const double ScrollAllowance = 20;

    /// <summary>星号列复算的次数上限。两轮就够（声明值 → 实际值），第三轮兜底。</summary>
    private const int MaxStarPasses = 3;

    private static readonly RoutedEvent ClickEvent =
        System.Windows.Controls.Primitives.ButtonBase.ClickEvent;

    private readonly ListView _list;
    private readonly GridView _view;
    private readonly TextBlock _empty;
    private readonly List<GridViewColumn> _starColumns = [];
    private readonly List<AuroraRowAction> _rowActions = [];

    private AuroraTableData _data = AuroraTableData.Empty;
    private bool _headerStyleApplied;
    private bool _starRecheckQueued;
    private int _starPasses;
    private ScrollViewer? _scrollHost;

    /// <summary>右键按下时命中的那一行；空白处按下则为 null，菜单随之不弹。</summary>
    private IReadOnlyDictionary<string, string>? _menuRow;

    public AuroraTable()
    {
        // 底色由窗格卡片提供；表格自己不再画一块白（UI 风格规范 §1）。
        Background = Brushes.Transparent;

        // 组件自带控件字典：被拖进浮动窗口后 Aurora.Table.* 仍要解析得到。
        AuroraComponentResources.Ensure(this);

        _view = new GridView { AllowsColumnReorder = false };
        _list = new ListView
        {
            View = _view,
            SelectionMode = SelectionMode.Single,
        };
        _list.SetResourceReference(StyleProperty, "Aurora.Table.ListView");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Aurora.Table.Row");
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _list.SizeChanged += (_, _) =>
        {
            _starPasses = 0;
            ApplyStarWidths();
        };
        _list.PreviewMouseRightButtonDown += OnRowRightButtonDown;
        _list.ContextMenuOpening += OnRowContextMenuOpening;

        _empty = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _empty.SetResourceReference(StyleProperty, "Aurora.Table.Empty");

        var surface = new Border { Child = new Grid { Children = { _list, _empty } } };
        surface.SetResourceReference(StyleProperty, "Aurora.Table.Surface");
        Content = surface;

        // GridView 是 DependencyObject 而非 FrameworkElement，拿不到 SetResourceReference；
        // 表头样式只能在进入可视树后按键查一次。它本身内部全用 DynamicResource 取色，
        // 因此查一次就够，主题切换仍然跟随。
        Loaded += (_, _) => ApplyHeaderStyle();
    }

    /// <summary>当前选中行；无选中时为 null。列里没有的键不会出现在这里。</summary>
    public IReadOnlyDictionary<string, string>? SelectedRow
        => _list.SelectedItem as IReadOnlyDictionary<string, string>;

    /// <summary>选中行变化。<see cref="SelectedRow"/> 已经是新值。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>行内按钮或右键菜单被点。事件里带着被操作的**那一行**，不必回头读选中态。</summary>
    public event EventHandler<AuroraRowActionEventArgs>? RowActionInvoked;

    /// <summary>选中行的序号；-1 表示无选中。可写，供程序驱动选择。</summary>
    public int SelectedIndex
    {
        get => _list.SelectedIndex;
        set => _list.SelectedIndex = value;
    }

    /// <summary>行数为 0 时显示的文案。这是空态**唯一**的可调项。</summary>
    public string EmptyText
    {
        get => _empty.Text;
        set => _empty.Text = value ?? "";
    }

    /// <summary>当前数据。</summary>
    public AuroraTableData Data => _data;

    /// <summary>行数，等于 <c>Data.RowCount</c>；供测试与状态栏读取。</summary>
    public int RowCount => _data.RowCount;

    /// <summary>列数，等于 <c>Data.ColumnCount</c>。行操作列不计入——它不是数据。</summary>
    public int ColumnCount => _data.ColumnCount;

    /// <summary>当前的行操作声明。</summary>
    public IReadOnlyList<AuroraRowAction> RowActions => _rowActions.ToList();

    /// <summary>
    /// 门禁用：列头文字与右键菜单。外观归组件、不对外开放，但"行操作真的出现了两个出口"
    /// 这条必须能被断言——否则它只能靠肉眼在真机上看，而那正是这一轮在消灭的验证方式。
    /// </summary>
    internal (IReadOnlyList<string> Headers, ContextMenu? Menu) Inspect()
        => (_view.Columns.Select(column => column.Header as string ?? "").ToList(), _list.ContextMenu);

    /// <summary>换一份数据并重建列。传 null 等同清空。</summary>
    public void SetData(AuroraTableData? data)
    {
        _data = data ?? AuroraTableData.Empty;
        _starPasses = 0;
        RebuildColumns();
        _list.ItemsSource = BuildRows(_data);
        _empty.Visibility = _data.RowCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyHeaderStyle();
        ApplyStarWidths();
    }

    /// <summary>只给行、列从数据推断。</summary>
    public void SetRows(IReadOnlyList<IReadOnlyDictionary<string, string>>? rows)
        => SetData(AuroraTableData.FromRows(rows));

    /// <summary>
    /// 声明行操作（REQ-UI-011）。同一份声明出两个口：
    /// <see cref="AuroraRowAction.Inline"/> 为 true 的进行内按钮列，全部动作都进右键菜单。
    /// 传 null 或空表清空——清空后右键不再弹菜单，而不是弹一个空菜单。
    /// </summary>
    public void SetRowActions(IReadOnlyList<AuroraRowAction>? actions)
    {
        _rowActions.Clear();
        foreach (var action in actions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(action.Id))
                _rowActions.Add(action);
        }

        _starPasses = 0;
        _list.ContextMenu = _rowActions.Count == 0 ? null : BuildContextMenu();
        RebuildColumns();
        ApplyStarWidths();
    }

    private void RebuildColumns()
    {
        _starColumns.Clear();
        _view.Columns.Clear();

        foreach (var column in _data.Columns)
        {
            var gridColumn = new GridViewColumn
            {
                Header = column.Title,
                CellTemplate = CellTemplate(column.Key),
            };

            if (column.FixedWidth is { } px)
                gridColumn.Width = px;
            else if (column.IsStar)
                _starColumns.Add(gridColumn);

            _view.Columns.Add(gridColumn);
        }

        var inline = _rowActions.Where(action => action.Inline).ToList();
        if (inline.Count == 0 || _data.ColumnCount == 0)
            return;

        // 行操作列钉在最右：它不是数据，宽度也不该由宿主的列宽声明来定。
        _view.Columns.Add(new GridViewColumn
        {
            Header = "操作",
            Width = InlineWidth(inline),
            CellTemplate = RowActionTemplate(inline),
        });
    }

    /// <summary>
    /// 单元格模板。不用 <c>DisplayMemberBinding</c>：那条路径下单元格文字继承行容器的字体，
    /// 组件就管不住字号与省略行为了，而"管住外观"正是本组件存在的理由。
    /// </summary>
    private static DataTemplate CellTemplate(string key)
    {
        var cell = new FrameworkElementFactory(typeof(TextBlock));
        cell.SetBinding(TextBlock.TextProperty, new Binding("[" + key + "]"));
        cell.SetResourceReference(StyleProperty, "Aurora.Table.Cell");

        // 被省略号吃掉的那半句不能就此消失：列窄的时候整整一列都可能只剩「前…」。
        // 提示的内容就是本格文字，开关到悬停那一刻再算——只有真的放不下才弹。
        cell.SetBinding(
            ToolTipProperty,
            new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self });
        cell.AddHandler(MouseEnterEvent, new MouseEventHandler(OnCellMouseEnter));

        return new DataTemplate { VisualTree = cell };
    }

    private static void OnCellMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is TextBlock cell)
            ToolTipService.SetIsEnabled(cell, IsTrimmed(cell));
    }

    /// <summary>
    /// 这一格的文字是不是真的放不下。
    ///
    /// 不用 <c>TextBlock.IsTextTrimmed</c>：那个属性只有 .NET Framework 4.8 的 WPF 有，
    /// 本仓是 net8.0-windows，写进 Style 触发器会在 XAML 编译期直接报错。
    /// 量一次文字宽度即可，而且只在指针进入时量。
    /// </summary>
    internal static bool IsTrimmed(TextBlock cell)
    {
        if (string.IsNullOrEmpty(cell.Text) || cell.ActualWidth <= 0)
            return false;

        var formatted = new FormattedText(
            cell.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            cell.FlowDirection,
            new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight, cell.FontStretch),
            cell.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(cell).PixelsPerDip);

        return formatted.Width > cell.ActualWidth + 0.5;
    }

    /// <summary>
    /// 行内按钮的单元格模板。按钮的 <c>DataContext</c> 由行继承而来，
    /// 因此点击时不必回头问"当前选中的是哪一行"——那正是 1.6.0 里
    /// "先选行、再下去点按钮"的来源。动作 id 挂在 <c>Tag</c> 上，一个处理器认所有按钮。
    /// </summary>
    private DataTemplate RowActionTemplate(IReadOnlyList<AuroraRowAction> actions)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        foreach (var action in actions)
        {
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(ContentControl.ContentProperty, action.Title);
            button.SetValue(TagProperty, action.Id);
            button.SetValue(ToolTipProperty, (object)(action.Summary ?? action.Id));
            button.SetResourceReference(
                StyleProperty,
                action.Danger ? "Aurora.Table.RowActionDanger" : "Aurora.Table.RowAction");
            button.AddHandler(ClickEvent, new RoutedEventHandler(OnRowActionClick));
            panel.AppendChild(button);
        }

        return new DataTemplate { VisualTree = panel };
    }

    private void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement element)
            return;
        if (element.Tag as string is not { Length: > 0 } id)
            return;
        if (element.DataContext is not IReadOnlyDictionary<string, string> row)
            return;
        Fire(id, row);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        // 菜单在自己的弹出窗口里，逻辑父级链断得早；不自带字典的话 Aurora.* 键解析不到，
        // 而 WPF 对解析不到的 DynamicResource 不抛异常，表现为"只有右键菜单长得不一样"。
        AuroraComponentResources.Ensure(menu);

        foreach (var action in _rowActions)
        {
            var item = new MenuItem
            {
                Header = action.Title,
                Tag = action.Id,
                ToolTip = action.Summary ?? action.Id,
            };
            if (action.Danger)
                item.SetResourceReference(ForegroundProperty, "Aurora.Brush.Danger");

            var id = action.Id;
            item.Click += (_, _) =>
            {
                if (_menuRow is { } row)
                    Fire(id, row);
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// 右键先把指针底下那一行选中，再让菜单弹出来。
    /// 不选中的话菜单看起来作用于"上一次选的行"，而那一行可能根本不在视野里。
    /// </summary>
    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _menuRow = null;
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (ItemsControl.ContainerFromElement(_list, source) is not ListViewItem item)
            return;
        if (item.Content is not IReadOnlyDictionary<string, string> row)
            return;

        _menuRow = row;
        item.IsSelected = true;
    }

    /// <summary>空白处右键不弹菜单：弹一个"点了什么都不会发生"的菜单比不弹更费解。</summary>
    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_menuRow == null)
            e.Handled = true;
    }

    /// <summary>供测试与键盘路径复用：按 id 触发某一行的动作。</summary>
    internal void Fire(string actionId, IReadOnlyDictionary<string, string> row)
    {
        var action = _rowActions.FirstOrDefault(
            candidate => candidate.Id.Equals(actionId, StringComparison.Ordinal));
        if (action == null)
            return;
        RowActionInvoked?.Invoke(this, new AuroraRowActionEventArgs(action, row));
    }

    /// <summary>行内按钮列的宽度按标题估算：中日韩字符按 14px，其余按 8px，另加边框与间距。</summary>
    private static double InlineWidth(IReadOnlyList<AuroraRowAction> actions)
    {
        var total = 8d;
        foreach (var action in actions)
        {
            var text = 0d;
            foreach (var character in action.Title)
                text += character > 0x2E80 ? 14 : 8;
            total += text + 22;
        }

        return total;
    }

    /// <summary>
    /// 行按列声明补齐：缺的键填空串。
    /// 不补齐的话字典索引器会抛 <c>KeyNotFoundException</c>，WPF 把它吞成一条绑定错误，
    /// 表现为"这一格莫名其妙是空的"，而输出窗口以外看不到任何线索。
    /// </summary>
    private static List<IReadOnlyDictionary<string, string>> BuildRows(AuroraTableData data)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>(data.RowCount);
        foreach (var source in data.Rows)
        {
            var row = new Dictionary<string, string>(data.ColumnCount, StringComparer.Ordinal);
            foreach (var column in data.Columns)
                row[column.Key] = source.TryGetValue(column.Key, out var cell) ? cell ?? "" : "";
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>列表模板里的滚动视图；进入可视树后才有，取到后缓存。</summary>
    private ScrollViewer? ScrollHost()
    {
        if (_scrollHost != null)
            return _scrollHost;
        if (!_list.IsLoaded)
            return null;

        _scrollHost = FindDescendant<ScrollViewer>(_list);
        return _scrollHost;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private void ApplyHeaderStyle()
    {
        if (_headerStyleApplied || !IsLoaded)
            return;
        if (TryFindResource("Aurora.GridHeader") is not Style header)
            return;
        _view.ColumnHeaderContainerStyle = header;
        _headerStyleApplied = true;
    }

    /// <summary>
    /// 星号列按剩余宽度均分。<c>GridView</c> 没有星号列的概念，只能自己算：
    /// 可用宽度减去定宽与自适应列的实际宽度，余下的平分给星号列。
    /// </summary>
    private void ApplyStarWidths()
    {
        if (_starColumns.Count == 0 || _list.ActualWidth <= 0)
            return;

        var taken = 0d;
        foreach (var column in _view.Columns)
        {
            if (_starColumns.Contains(column))
                continue;
            // 自适应列的 Width 是 NaN，直接相加会把总宽污染成 NaN，星号列随之算不出来。
            if (column.ActualWidth > 0)
                taken += column.ActualWidth;
            else if (!double.IsNaN(column.Width))
                taken += column.Width;
        }

        // 可用宽度以**滚动视口**为准，拿不到时才退回"表宽减去滚动条余量"。
        // 视口宽度是权威值：它已经扣掉了纵向滚动条，也扣掉了列表自己的边框与内距，
        // 而那几像素正是 1.7.0 那条横向滚动条的来源。
        var scroll = ScrollHost();
        var available = scroll is { ViewportWidth: > 0 }
            ? scroll.ViewportWidth
            : _list.ActualWidth - ScrollAllowance;

        // 上一轮排完之后仍然溢出多少，直接减掉。不去追问是谁多占的——
        // 表头最小宽度、分隔条命中区、行操作按钮的外边距都可能贡献几像素，
        // 逐个建模只会漏掉下一个。反馈一次就够，且下一轮会验证它。
        var overflow = scroll is { ViewportWidth: > 0 }
            ? Math.Max(0, scroll.ExtentWidth - scroll.ViewportWidth)
            : 0;

        var remaining = available - taken - overflow;
        var share = Math.Max(MinStarWidth, remaining / _starColumns.Count);

        foreach (var column in _starColumns)
            column.Width = share;

        // 定宽列的**实际**宽度要等一次布局才知道：列宽声明小于表头文字所需时，
        // GridView 会把那一列撑开。几列各多出两三像素，加起来就够让表格长出一条
        // 横向滚动条（1.7.0 真机上命令集那张表就是这么来的）。
        // 所以要在布局跑完之后按实际宽度再算一遍。
        //
        // 收敛用**次数**兜底而不是"宽度不再变化"：后者会在"这一轮刚好没变、
        // 而同一轮布局又把定宽列撑开了"时提前停下，正是本缺陷的成因。
        if (_starPasses >= MaxStarPasses || _starRecheckQueued)
            return;

        _starRecheckQueued = true;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                _starRecheckQueued = false;
                _starPasses++;
                ApplyStarWidths();
            }));
    }
}
