using System.Runtime.CompilerServices;
using HistoryAurora.Shell.Components.Actions;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Table;

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
    /// <summary>
    /// 列宽下限：再窄也要看得见表头文字。**这是软下限**——列多到连下限都排不下时
    /// 按可用宽度均分，宁可挤也不长出横向滚动条（REQ-UI-039）。
    /// </summary>
    private const double MinColumnWidth = 48;

    /// <summary>给纵向滚动条留出的余量：拿不到视口宽度时的退路。</summary>
    private const double ScrollAllowance = 20;

    /// <summary><c>"*"</c> 列的权重。相当于声明了 200px 的列，只是不必写死数字。</summary>
    private const double StarWeight = 200;

    /// <summary>没声明宽度时的权重。</summary>
    private const double AutoWeight = 120;

    private const int MaxWidthPasses = AuroraTableLayoutGuard.MaxWidthPasses;
    private const double WidthEpsilon = AuroraTableLayoutGuard.WidthEpsilon;

    private static readonly RoutedEvent ClickEvent =
        System.Windows.Controls.Primitives.ButtonBase.ClickEvent;

    private readonly ConditionalWeakTable<object, Dictionary<string, AuroraCommandActivity>> _activities = new();
    private readonly ListView _list;
    private readonly GridView _view;
    private readonly TextBlock _empty;
    private readonly TextBlock _status;
    private readonly ObservableCollection<IReadOnlyDictionary<string, string>> _rows = [];
    private DateTimeOffset? _updatedAt;
    private bool _changingRows;
    /// <summary>数据列的权重。行操作列不在其中——它不参与分摊。</summary>
    private readonly Dictionary<GridViewColumn, double> _weights = [];

    /// <summary>数据列的取值键，用于记忆列序。行操作列不在其中。</summary>
    private readonly Dictionary<GridViewColumn, string> _columnKeys = [];
    private readonly List<AuroraRowAction> _rowActions = [];

    private AuroraTableData _data = AuroraTableData.Empty;
    private bool _headerStyleApplied;
    private bool _widthPassQueued;
    private int _widthPasses;
    private int _sizeChangeRestarts;
    private int _burstRestarts;
    private long _burstStartMs;
    private GridViewColumn? _actionColumn;
    private int _applyingWidths;

    /// <summary>占满剩余宽度的那一列；它钉在数据列的最右，拖不动（REQ-UI-062）。</summary>
    private GridViewColumn? _starColumn;

    private ScrollViewer? _scrollHost;

    /// <summary>列序记忆的落点；未接时列序只在本次可视树里有效。</summary>
    private IColumnOrderStore? _orderStore;

    /// <summary>本表在列序台账里的身份，形如 <c>模块/页面/节点</c>。</summary>
    private string? _orderKey;

    /// <summary>正在由组件自己动列集合；期间不把变化当成用户拖动。</summary>
    private bool _reordering;

    /// <summary>右键按下时命中的那一行；空白处按下则为 null，菜单随之不弹。</summary>
    private IReadOnlyDictionary<string, string>? _menuRow;

    public AuroraTable()
    {
        // 底色由窗格卡片提供；表格自己不再画一块白（UI 风格规范 §1）。
        Background = Brushes.Transparent;

        // 组件自带控件字典：被拖进浮动窗口后 Aurora.Table.* 仍要解析得到。
        AuroraComponentResources.Ensure(this);

        // 列可以拖着换位置（REQ-UI-062）。**一律开，不按声明开**：
        // 表格外观本来就是"传不进来"的，而列序是看的人当下的习惯，不是页面作者的决定。
        _view = new GridView { AllowsColumnReorder = true };
        ((INotifyCollectionChanged)_view.Columns).CollectionChanged += OnColumnsChanged;
        _list = new ListView
        {
            View = _view,
            SelectionMode = SelectionMode.Single,
        };
        _list.SetResourceReference(StyleProperty, "Aurora.Table.ListView");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Aurora.Table.Row");
        _list.SelectionChanged += (_, _) => { if (!_changingRows) SelectionChanged?.Invoke(this, EventArgs.Empty); };
        _list.ItemsSource = _rows;
        _list.PreviewMouseRightButtonDown += OnRowRightButtonDown;
        _list.ContextMenuOpening += OnRowContextMenuOpening;
        SizeChanged += OnHostSizeChanged;

        _empty = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _empty.SetResourceReference(StyleProperty, "Aurora.Table.Empty");

        var surface = new Border { Child = new Grid { Children = { _list, _empty } } };
        surface.SetResourceReference(StyleProperty, "Aurora.Table.Surface");
        _status = new TextBlock { Margin = new Thickness(8, 4, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(StyleProperty, "Aurora.Text.Caption");
        var layout = (Grid)surface.Child;
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_status, 1);
        layout.Children.Add(_status);
        Content = surface;
        ShowStatus("尚未加载");

        // GridView 是 DependencyObject 而非 FrameworkElement，拿不到 SetResourceReference；
        // 表头样式只能在进入可视树后按键查一次。它本身内部全用 DynamicResource 取色，
        // 因此查一次就够，主题切换仍然跟随。
        Loaded += (_, _) =>
        {
            ApplyHeaderStyle();
            RestartWidthLayout();
        };
    }

    /// <summary>
    /// 当前选中行；无选中时为 null。
    /// **取数返回的键一个不少**——没声明成列的键照样在里面（REQ-UI-058）。
    /// </summary>
    public IReadOnlyDictionary<string, string>? SelectedRow
        => _list.SelectedItem as IReadOnlyDictionary<string, string>;

    /// <summary>选中行变化。<see cref="SelectedRow"/> 已经是新值。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>行内按钮或右键菜单被点。事件里带着被操作的**那一行**，不必回头读选中态。</summary>
    public event EventHandler<AuroraRowActionEventArgs>? RowActionInvoked;

    /// <summary>可点击单元格被触发。事件同时携带列键与被点行，不依赖当前选中态。</summary>
    public event EventHandler<AuroraCellActionEventArgs>? CellActionInvoked;

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
    public void SetData(AuroraTableData? data) => SetSnapshot(data, null);

    internal void SetSnapshot(AuroraTableData? data, string? rowKey)
    {
        data ??= AuroraTableData.Empty;
        var columnsChanged = !_data.Columns.SequenceEqual(data.Columns);
        _data = data;
        if (columnsChanged)
            RebuildColumns();
        var rows = BuildRows(_data);
        var selected = SelectedIndex;
        var oldSelection = SelectedRow;
        var selectedKey = rowKey == null ? null : oldSelection?.GetValueOrDefault(rowKey);
        _changingRows = true;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (i >= _rows.Count)
                    _rows.Add(rows[i]);
                else if (!SameRow(_rows[i], rows[i]))
                    _rows[i] = rows[i];
            }
            while (_rows.Count > rows.Count)
                _rows.RemoveAt(_rows.Count - 1);
            if (selected >= 0 && selected < _rows.Count)
                SelectedIndex = selected;
            if (rowKey != null && selectedKey != null)
                _list.SelectedItem = _rows.FirstOrDefault(row => row.GetValueOrDefault(rowKey) == selectedKey);
        }
        finally { _changingRows = false; }
        NotifySelection(oldSelection);
        _empty.Visibility = _data.RowCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyHeaderStyle();
        RestartWidthLayout();
        _updatedAt = DateTimeOffset.Now;
        ShowStatus("已更新");
    }

    private void NotifySelection(IReadOnlyDictionary<string, string>? previous)
    {
        var current = SelectedRow;
        if (previous == null ? current != null : current == null || !SameRow(previous, current))
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameRow(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>常驻显示加载状态；保留最后成功更新时间和当前行数。</summary>
    public void ShowStatus(string message)
    {
        _status.Text = $"{message} · {RowCount} 条 · 更新时间：{(_updatedAt is { } time ? time.ToString("yyyy-MM-dd HH:mm:ss") : "—")}";
        _status.ToolTip = _status.Text;
    }

    /// <summary>按唯一行键局部更新；upserts 合并字段，新增行追加，removes 删除行。输入无效时整批拒绝。</summary>
    public void ApplyDelta(string rowKey, IReadOnlyList<IReadOnlyDictionary<string, string>> upserts, IReadOnlyList<string>? removes = null)
    {
        Dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        ArgumentNullException.ThrowIfNull(upserts);
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].TryGetValue(rowKey, out var key) || string.IsNullOrEmpty(key) || !current.TryAdd(key, i))
                throw new ArgumentException("现有行缺少唯一行键：" + rowKey);
        }
        var updates = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in upserts)
        {
            if (row == null || !row.TryGetValue(rowKey, out var key) || string.IsNullOrEmpty(key) || !updates.TryAdd(key, row))
                throw new ArgumentException("更新行缺少唯一行键或行键重复：" + rowKey);
        }
        var deleted = new HashSet<string>(removes ?? [], StringComparer.Ordinal);
        if (deleted.Overlaps(updates.Keys))
            throw new ArgumentException("同一行不能同时更新与删除");
        var oldSelection = SelectedRow;
        var selectedKey = oldSelection?.GetValueOrDefault(rowKey);
        _changingRows = true;
        try
        {
            foreach (var (key, patch) in updates)
            {
                var row = current.TryGetValue(key, out var index)
                    ? new Dictionary<string, string>(_rows[index], StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in patch)
                    row[pair.Key] = pair.Value ?? "";
                foreach (var column in _data.Columns)
                    row.TryAdd(column.Key, "");
                if (current.ContainsKey(key))
                {
                    if (!SameRow(_rows[index], row))
                        _rows[index] = row;
                }
                else
                    _rows.Add(row);
            }
            foreach (var index in deleted.Where(current.ContainsKey).Select(key => current[key]).OrderDescending())
                _rows.RemoveAt(index);
            _data = AuroraTableData.Create(_data.Columns, _rows);
            if (selectedKey != null)
                _list.SelectedItem = _rows.FirstOrDefault(row => row.GetValueOrDefault(rowKey) == selectedKey);
        }
        finally { _changingRows = false; }
        NotifySelection(oldSelection);
        _empty.Visibility = RowCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatedAt = DateTimeOffset.Now;
        ShowStatus("已更新");
        RestartWidthLayout();
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

        RestartWidthLayout();
        _list.ContextMenu = _rowActions.Count == 0 ? null : BuildContextMenu();
        RebuildColumns();
        RestartWidthLayout();
    }

    /// <summary>
    /// 接上列序记忆（REQ-UI-062）。<paramref name="key"/> 是这张表的身份，
    /// 形如 <c>模块/页面/节点</c>；没有身份的表（描述里没写 id）不接，
    /// 那样的表拖完也认不出是哪一张，记下来只会张冠李戴。
    /// </summary>
    public void UseColumnOrder(IColumnOrderStore? store, string? key)
    {
        _orderStore = string.IsNullOrWhiteSpace(key) ? null : store;
        _orderKey = _orderStore == null ? null : key;
        RebuildColumns();
        RestartWidthLayout();
    }

    /// <summary>
    /// 按记住的列序重排声明列。
    ///
    /// 三条对齐规则，都是为了「声明改了、记录还是旧的」这一种情况：
    /// 记录里有、声明里没有的键**丢掉**；声明里有、记录里没有的列**按声明顺序补在后面**；
    /// 星号列无论记录怎么写都回到最后——它是"占满剩余"的那一列，不在最后就没有"剩余"可占。
    /// </summary>
    private List<AuroraTableColumn> OrderedColumns()
    {
        var declared = _data.Columns.ToList();
        var remembered = _orderStore?.Read(_orderKey ?? "") ?? [];
        if (remembered.Count > 0)
        {
            var byKey = declared.ToDictionary(column => column.Key, StringComparer.Ordinal);
            var ordered = new List<AuroraTableColumn>(declared.Count);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in remembered)
            {
                if (byKey.TryGetValue(key, out var column) && taken.Add(key))
                    ordered.Add(column);
            }

            foreach (var column in declared)
            {
                if (!taken.Contains(column.Key))
                    ordered.Add(column);
            }

            declared = ordered;
        }

        var star = declared.FirstOrDefault(column => column.IsStar);
        if (star != null && declared[^1] != star)
        {
            declared.Remove(star);
            declared.Add(star);
        }

        return declared;
    }

    private void RebuildColumns()
    {
        _weights.Clear();
        _columnKeys.Clear();
        _actionColumn = null;
        _starColumn = null;

        // 清空与重建也会打到 CollectionChanged 上；不挡住的话每次换数据都会被当成
        // 一次用户拖动，把声明顺序当成"用户拖出来的顺序"写回台账。
        _reordering = true;
        try
        {
            _view.Columns.Clear();

            foreach (var column in OrderedColumns())
            {
                var gridColumn = new GridViewColumn
                {
                    Header = column.Title,
                    CellTemplate = column.CellAction == null
                        ? CellTemplate(column.Key)
                        : CellActionTemplate(column),
                };

                // 声明的数字是**权重**，不是像素（REQ-UI-039）：表格永远铺满可用宽度，
                // 各列按权重分摊。写 150 / 70 的那张表，比例仍是 150:70，只是随宽度缩放。
                _weights[gridColumn] = column.FixedWidth ?? (column.IsStar ? StarWeight : AutoWeight);
                _columnKeys[gridColumn] = column.Key;
                if (column.IsStar)
                    _starColumn = gridColumn;

                _view.Columns.Add(gridColumn);
            }
        }
        finally
        {
            _reordering = false;
        }

        var inline = _rowActions.Where(action => action.Inline).ToList();
        if (inline.Count == 0 || _data.ColumnCount == 0)
            return;

        // 行操作列钉在最右：它不是数据，宽度也不该由宿主的列宽声明来定。
        // 它**不参与**按比例缩放——按钮排不下就点不着，缩放对它没有意义；分摊时先扣掉。
        _actionColumn = new GridViewColumn
        {
            Header = "操作",
            Width = InlineWidth(inline),
            CellTemplate = RowActionTemplate(inline),
        };

        _reordering = true;
        try
        {
            _view.Columns.Add(_actionColumn);
        }
        finally
        {
            _reordering = false;
        }
    }

    /// <summary>
    /// 用户拖完一列之后（REQ-UI-062）。
    ///
    /// **修正必须排到下一拍**：这是 <c>ObservableCollection</c> 的通知过程中，
    /// 在这里再动一次集合会抛「不允许重入」。排到 Background 优先级上，
    /// 拖动的那一帧先画完，再把星号列拨回最右。
    /// </summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_reordering || e.Action != NotifyCollectionChangedAction.Move)
            return;

        _reordering = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                KeepStarLast();
            }
            finally
            {
                _reordering = false;
            }

            SaveColumnOrder();

            // 列序变了，"最后一列吃余数"这条规则落到的就是另一列，必须重排一次宽度。
            RestartWidthLayout();
        }));
    }

    /// <summary>
    /// 把星号列拨回数据列的最右。
    ///
    /// 这一条同时实现了两件事：星号列自己拖不走（拖了就被拨回来），
    /// 别的列也落不到它右边（落过去会把它顶开，随即被拨回最右）。
    /// 一条规则、一处实现——两件事分开写的话，总有一种拖法两边都没盖住。
    /// </summary>
    private void KeepStarLast()
    {
        if (_starColumn is not { } star)
            return;

        var last = _view.Columns.Count - 1;
        if (_actionColumn != null)
            last--;
        if (last < 0)
            return;

        var index = _view.Columns.IndexOf(star);
        if (index >= 0 && index != last)
            _view.Columns.Move(index, last);
    }

    /// <summary>把当前列序记进台账。行操作列不记——它不是数据列，位置也不归用户管。</summary>
    private void SaveColumnOrder()
    {
        if (_orderStore is not { } store || _orderKey is not { Length: > 0 } key)
            return;

        var keys = new List<string>(_view.Columns.Count);
        foreach (var column in _view.Columns)
        {
            if (_columnKeys.TryGetValue(column, out var columnKey))
                keys.Add(columnKey);
        }

        store.Write(key, keys);
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

    /// <summary>
    /// 可点击单元格使用真正的 Button，因此鼠标、Enter 与 Space 走同一条 WPF 命令路径。
    /// 动作 id 和列键挂在 Tag 上；行仍由按钮继承的 DataContext 提供。
    /// </summary>
    private DataTemplate CellActionTemplate(AuroraTableColumn column)
    {
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(TagProperty, new CellActionTag(column.CellAction!.Id, column.Key));
        button.SetValue(
            ToolTipProperty,
            string.IsNullOrWhiteSpace(column.CellAction.Summary)
                ? $"点击执行 {column.CellAction.Id}"
                : column.CellAction.Summary);
        button.SetResourceReference(
            StyleProperty,
            column.CellAction.Danger ? "Aurora.Table.CellActionDanger" : "Aurora.Table.CellAction");
        button.AddHandler(ClickEvent, new RoutedEventHandler(OnCellActionClick));
        button.AddHandler(LoadedEvent, new RoutedEventHandler(OnActionLoaded));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("[" + column.Key + "]"));
        text.SetResourceReference(StyleProperty, "Aurora.Table.CellActionText");
        button.AppendChild(text);

        return new DataTemplate { VisualTree = button };
    }

    private void OnCellActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: CellActionTag tag } element)
            return;
        if (element.DataContext is not IReadOnlyDictionary<string, string> row)
            return;
        FireCell(tag.ActionId, tag.ColumnKey, row);
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
            cell.Foreground ?? Brushes.Transparent, // 只量宽度，画刷不参与；用单元格自己的前景，免得这里冒出一个写死的颜色
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
            button.AddHandler(LoadedEvent, new RoutedEventHandler(OnActionLoaded));
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
        var activity = Activity(row, actionId);
        _ = activity.RunAsync(() =>
        {
            var args = new AuroraRowActionEventArgs(action, row);
            RowActionInvoked?.Invoke(this, args);
            return args.Completion ?? Task.FromResult(true);
        });
    }

    /// <summary>供测试与键盘路径复用：按动作和列键触发某一格。</summary>
    internal void FireCell(
        string actionId,
        string columnKey,
        IReadOnlyDictionary<string, string> row)
    {
        var action = _data.Columns
            .FirstOrDefault(column => column.Key.Equals(columnKey, StringComparison.Ordinal))
            ?.CellAction;
        if (action == null || !action.Id.Equals(actionId, StringComparison.Ordinal))
            return;
        if (!row.TryGetValue(columnKey, out var value) || string.IsNullOrWhiteSpace(value))
            return;
        var activity = Activity(row, actionId);
        _ = activity.RunAsync(() =>
        {
            var args = new AuroraCellActionEventArgs(action, columnKey, row);
            CellActionInvoked?.Invoke(this, args);
            return args.Completion ?? Task.FromResult(true);
        });
    }

    private AuroraCommandActivity Activity(object row, string actionId)
    {
        var map = _activities.GetOrCreateValue(row);
        if (!map.TryGetValue(actionId, out var activity))
            map[actionId] = activity = new AuroraCommandActivity();
        return activity;
    }

    private void OnActionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.DataContextChanged -= OnActionContextChanged;
        element.DataContextChanged += OnActionContextChanged;
        BindActivity(element);
    }

    private void OnActionContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => BindActivity((FrameworkElement)sender);

    private void BindActivity(FrameworkElement element)
    {
        var id = element.Tag is CellActionTag tag ? tag.ActionId : element.Tag as string;
        AuroraCommandActivity.SetActivity(element, element.DataContext is { } row && id != null ? Activity(row, id) : null);
    }

    private sealed record CellActionTag(string ActionId, string ColumnKey);

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
    ///
    /// **补齐，不是裁剪**（REQ-UI-058）：没被声明成列的键**原样留着**。
    /// 表格只画声明过的列，但 <see cref="SelectedRow"/> 与行操作拿到的是整行——
    /// 命令集把完整指令名 <c>name</c> 从列里去掉之后，右键菜单的四条动作、
    /// 指令详情页与对外契约 <c>IShellCommandWorkbenchHost.CommandSelection</c> 都靠这一条活着。
    /// 裁掉的话它们会一起断，而断法是"点了没反应"，不是报错。
    /// </summary>
    private static List<IReadOnlyDictionary<string, string>> BuildRows(AuroraTableData data)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>(data.RowCount);
        foreach (var source in data.Rows)
        {
            var row = new Dictionary<string, string>(source, StringComparer.Ordinal);
            foreach (var column in data.Columns)
            {
                if (!row.ContainsKey(column.Key))
                    row[column.Key] = "";
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>列表模板里的滚动视图；进入可视树后才有，取到后缓存。</summary>
    /// <summary>
    /// 表格框架比列宽之和多占的那几像素。
    ///
    /// 只看外层 ScrollViewer 是不够的：<c>GridView</c> 的表头行另有一个自己的滚动视图，
    /// 多出来的像素恰恰在它那里——表头末尾有一个约 2px 的占位列，加上边框，
    /// 各列加起来正好等于视口时表头仍会超出 6px 左右。
    ///
    /// 量的是「内容宽 － 列宽之和」而不是「内容宽 － 视口宽」：前者与我们设了多宽无关，
    /// 因此每轮重量一次即可；后者随上一轮设的宽度变化，累加就会扣重。
    /// </summary>
    private double ChromeOverhead()
    {
        var columns = 0d;
        foreach (var column in _view.Columns)
        {
            if (column.ActualWidth > 0)
                columns += column.ActualWidth;
            else if (!double.IsNaN(column.Width))
                columns += column.Width;
        }

        if (columns <= 0)
            return 0;

        var extent = 0d;
        foreach (var scroll in Descendants<ScrollViewer>(_list))
        {
            if (scroll.ViewportWidth > 0)
                extent = Math.Max(extent, scroll.ExtentWidth);
        }

        return Math.Max(0, extent - columns);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

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

    /// <summary>外部宽度变了才重来：内部调列宽也会传播 SizeChanged。</summary>
    private void RestartWidthLayout()
    {
        _widthPasses = 0;
        QueueWidthPass();
    }

    private void QueueWidthPass()
    {
        if (_weights.Count == 0 || _widthPassQueued)
            return;

        _widthPassQueued = true;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            new Action(() =>
            {
                _widthPassQueued = false;
                ApplyColumnWidths();
            }));
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只看外框宽度。ListView 的 SizeChanged 会把列宽回写算成外部分配，
        // 打开带表的页面时振幅是整页宽度，24px 门槛挡不住。
        if (!e.WidthChanged || _applyingWidths > 0 || _widthPassQueued)
            return;

        var now = Environment.TickCount64;
        if (now - _burstStartMs > 250)
        {
            _burstStartMs = now;
            _burstRestarts = 0;
        }

        var delta = e.NewSize.Width - e.PreviousSize.Width;
        if (!AuroraTableLayoutGuard.ShouldRestartWidthLayout(
                delta, _widthPasses, _sizeChangeRestarts, _burstRestarts))
            return;

        _burstRestarts++;
        if (Math.Abs(delta) >= AuroraTableLayoutGuard.ExternalResizeEpsilon)
            _sizeChangeRestarts = 0;
        else
            _sizeChangeRestarts++;
        RestartWidthLayout();
    }

    private void ApplyColumnWidths()
    {
        _applyingWidths++;
        try
        {
            ApplyColumnWidthsCore();
        }
        finally
        {
            _applyingWidths--;
        }
    }

    /// <summary>
    /// 列宽分摊：可用宽度按权重切给各数据列，切完正好铺满。
    ///
    /// 三条硬约束（REQ-UI-039）：
    /// <list type="number">
    ///   <item><b>不留右侧空扩展区。</b>最后一列取「可用宽度减去前面各列」的余数，
    ///         而不是自己那份权重——按比例算完再相加，取整误差会在右边剩下一条缝。</item>
    ///   <item><b>不长横向滚动条。</b>列表已禁用横向滚动；宽度不够时下限自动放低，
    ///         宁可挤也不溢出。</item>
    ///   <item>行操作列不参与缩放，先扣掉。</item>
    /// </list>
    /// </summary>
    private void ApplyColumnWidthsCore()
    {
        // **按可视顺序取列，不按声明顺序**（REQ-UI-062）：下面「最后一列吃余数」
        // 那一条说的是屏幕上最右边那一列。列可以被拖着换位置之后，
        // 照声明顺序算会把余数发给一列画在中间的列，右边就空出一条缝。
        var ordered = new List<(GridViewColumn Column, double Weight)>(_weights.Count);
        foreach (var column in _view.Columns)
        {
            if (_weights.TryGetValue(column, out var weight))
                ordered.Add((column, weight));
        }

        if (ordered.Count == 0 || _list.ActualWidth <= 0)
            return;

        // 可用宽度以**滚动视口**为准：它已经扣掉纵向滚动条、列表边框与内距。
        var scroll = ScrollHost();
        var available = scroll is { ViewportWidth: > 0 }
            ? scroll.ViewportWidth
            : _list.ActualWidth - ScrollAllowance;

        if (_actionColumn is { } actions)
            available -= actions.ActualWidth > 0 ? actions.ActualWidth : actions.Width;

        // 表头最小宽度、分隔条命中区、单元格内距都可能各贡献一两像素，
        // 这些**算不出来、只量得到**：布局跑完后按实测溢出收紧，直到不再溢出。
        // 只收紧不放大，因此不会在两种宽度之间来回跳。
        // 表格自身的框架开销：表头末尾那个约 2px 的占位列、边框、单元格内距。
        // 这些**算不出来、只量得到**，但它与列宽无关，因此每轮重新量、不累加——
        // 累加会把同一份开销扣两次（量到的是上一轮布局，慢一拍），右边就空出一条缝。
        available -= ChromeOverhead();

        // ActualWidth 要等一次布局才有值，所以第一轮量不到开销，必须再跑一轮。
        if (_widthPasses < MaxWidthPasses)
        {
            _widthPasses++;
            QueueWidthPass();
        }
        else
        {
            _burstRestarts = 0;
        }

        LockRightEdge();

        if (available <= 0)
            return;

        // 下限是软的：列多到连下限都排不下时按均分让位，不能因为守住下限而溢出。
        var floor = Math.Min(MinColumnWidth, available / ordered.Count);
        var total = ordered.Sum(entry => entry.Weight);
        if (total <= 0)
            return;

        var used = 0d;
        for (var index = 0; index < ordered.Count; index++)
        {
            var (column, weight) = ordered[index];
            var width = index == ordered.Count - 1
                ? available - used
                : Math.Max(floor, Math.Round(available * weight / total));

            width = Math.Max(floor, width);
            used += width;

            if (double.IsNaN(column.Width) || Math.Abs(column.Width - width) >= WidthEpsilon)
                column.Width = width;
        }
    }

    /// <summary>
    /// 表格右缘锁死（1.26.0，REQ-UI-126）。
    ///
    /// 列宽是按比例算出来的，最右那条线**永远等于表格右缘**：最后一列数据列吃余数，
    /// 行操作列宽度固定。于是这两列右侧的拖拽线拖了也没用——拖完下一轮分摊又拨回去，
    /// 留着只会让表头最右边多出一条竖线，看上去像「右边还多注册了一列空的」。
    /// 从最后一列数据列起，右侧拖拽线一律收起；其余列的拖拽线照常可用。
    ///
    /// 表头容器由 <c>GridViewHeaderRowPresenter</c> 自己生成、列一拖就换位，
    /// 所以每轮分摊都按当前可视顺序重新判一次，而不是只在建列时设一次。
    /// </summary>
    private void LockRightEdge()
    {
        var lastData = -1;
        for (var index = _view.Columns.Count - 1; index >= 0; index--)
        {
            if (!_weights.ContainsKey(_view.Columns[index]))
                continue;
            lastData = index;
            break;
        }

        foreach (var header in Descendants<GridViewColumnHeader>(_list))
        {
            if (header.Column is not { } column)
                continue;
            if (header.Template?.FindName("PART_HeaderGripper", header) is not System.Windows.Controls.Primitives.Thumb gripper)
                continue;

            var locked = lastData >= 0 && _view.Columns.IndexOf(column) >= lastData;
            gripper.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

            // 先摘后挂：同一个拖拽线每轮都会走到这里，不能越挂越多。
            // 必须 handledEventsToo：GridView 自己的改宽处理器会把 DragDelta 标成已处理，
            // 普通 += 订阅因此一次都收不到。
            gripper.RemoveHandler(System.Windows.Controls.Primitives.Thumb.DragDeltaEvent, GripperDragDelta);
            gripper.AddHandler(System.Windows.Controls.Primitives.Thumb.DragDeltaEvent, GripperDragDelta, handledEventsToo: true);
        }
    }

    /// <summary>
    /// 用户拖了某一列的右侧线（REQ-UI-126）。
    ///
    /// GridView 自己的处理器负责把那一列改宽；这里负责「右缘不动」：
    /// 差额全由最后一列数据列吸收，最后一列挤到下限时反过来收住被拖的那一列。
    /// 拖完把各列当前宽度记成新的权重——之后窗口缩放按用户拖出来的比例走，
    /// 不会在下一次分摊时弹回声明比例。宽度不记进台账（REQ-UI-062 只记顺序）。
    /// </summary>
    private System.Windows.Controls.Primitives.DragDeltaEventHandler GripperDragDelta
        => _gripperDragDelta ??= OnGripperDragDelta;

    private System.Windows.Controls.Primitives.DragDeltaEventHandler? _gripperDragDelta;

    private void OnGripperDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.TemplatedParent is not GridViewColumnHeader { Column: { } dragged })
            return;

        // 与 GridView 自己的改宽处理器谁先谁后取决于挂接顺序（表头换模板会重挂），
        // 不能指望它已经跑过：排到 Render 优先级上，那时它一定已经把被拖列改宽了。
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => KeepRightEdge(dragged)));
    }

    private void KeepRightEdge(GridViewColumn dragged)
    {

        var ordered = _view.Columns.Where(_weights.ContainsKey).ToList();
        if (ordered.Count < 2 || ordered[^1] == dragged || !_weights.ContainsKey(dragged))
            return;

        var scroll = ScrollHost();
        if (scroll is not { ViewportWidth: > 0 })
            return;

        var available = scroll.ViewportWidth - ChromeOverhead();
        if (_actionColumn is { } actions)
            available -= actions.ActualWidth > 0 ? actions.ActualWidth : actions.Width;

        var floor = Math.Min(MinColumnWidth, available / ordered.Count);
        var last = ordered[^1];

        _applyingWidths++;
        try
        {
            // ChromeOverhead 量的是上一拍的布局，拖动途中与列宽之和相减会慢一拍；
            // 这里直接用「视口 － 框架 － 其余列当前宽度」，与分摊那一侧同一个口径。
            var others = ordered.Where(column => column != last).Sum(WidthOf);
            var remainder = available - others;
            if (remainder < floor)
            {
                dragged.Width = Math.Max(floor, WidthOf(dragged) - (floor - remainder));
                remainder = floor;
            }

            last.Width = Math.Max(floor, remainder);
            foreach (var column in ordered)
                _weights[column] = WidthOf(column);
        }
        finally
        {
            _applyingWidths--;
        }
    }

    private static double WidthOf(GridViewColumn column)
        => double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
}
