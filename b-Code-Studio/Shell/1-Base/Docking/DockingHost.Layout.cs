using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using System.Xml.Linq;
using AvalonDock.Controls;
using AvalonDock.Layout.Serialization;
using AvalonDock.Layout;
using AvalonDock;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 布局树这一面：默认布局怎么搭、一页放到哪一格、一格只留一页、比例怎么施加、中央区坏了怎么修。
/// 对外的每一条命令最终都落到这里的 <see cref="PlaceAtSide"/> 与 <see cref="PutInPane"/>。
/// </summary>
internal sealed partial class DockingHost
{
    /// <summary>
    /// 默认布局：每个方位一格，一格一页（REQ-UI-100）。同一方位声明了好几页时，
    /// 第一页露面，其余放进同一格后隐藏——AvalonDock 记住那一格，之后显示它就回到这里、顶掉当时那一页。
    ///
    /// 1.20.2 起所有页一律是工具页（<c>LayoutAnchorable</c>）：命令集不再有文档身份，
    /// 它与模块页、Aurora 自持页走同一条路，只是默认方位在中央。
    /// </summary>
    private void BuildDefaultLayout()
    {
        _preserveDefaultRatioOnSeed.Clear();
        _hiddenCenterIds.Clear();
        var docPane = new CenterDocumentPane();

        var centerColumn = new LayoutPanel(docPane) { Orientation = Orientation.Vertical };
        var rootPanel = new LayoutPanel(centerColumn) { Orientation = Orientation.Horizontal };
        var root = new LayoutRoot { RootPanel = rootPanel };
        _manager.Layout = root;

        double leftRatio = 0, rightRatio = 0, topRatio = 0, bottomRatio = 0;

        LayoutAnchorablePane MakeSidePane(IEnumerable<ToolWindowDescriptor> group)
        {
            var pane = new LayoutAnchorablePane();
            foreach (var d in group)
                pane.Children.Add(CreateAnchorable(d));
            return pane;
        }

        var bySide = _descriptors
            .Where(d => d.DefaultSide is not DockSide.Tab and not DockSide.Center)
            .GroupBy(d => d.DefaultSide)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (bySide.TryGetValue(DockSide.Left, out var lefts))
        {
            leftRatio = lefts.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(lefts);
            pane.DockWidth = Star(leftRatio);
            rootPanel.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Right, out var rights))
        {
            rightRatio = rights.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(rights);
            pane.DockWidth = Star(rightRatio);
            rootPanel.Children.Add(pane);
        }

        if (bySide.TryGetValue(DockSide.Top, out var tops))
        {
            topRatio = tops.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(tops);
            pane.DockHeight = Star(topRatio);
            centerColumn.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Bottom, out var bottoms))
        {
            bottomRatio = bottoms.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(bottoms);
            pane.DockHeight = Star(bottomRatio);
            centerColumn.Children.Add(pane);
        }

        foreach (var descriptor in _descriptors.Where(d => d.DefaultSide == DockSide.Center))
            docPane.Children.Add(CreateAnchorable(descriptor));

        centerColumn.DockWidth = Star(Math.Max(1 - leftRatio - rightRatio, 0.1));
        docPane.DockHeight = Star(Math.Max(1 - topRatio - bottomRatio, 0.1));

        // 默认不显示的页先藏起来，再按「每格第一页露面」整理，最后放跟随目标位置的页（DockSide.Tab）。
        foreach (var d in _descriptors.Where(d => !d.DefaultVisible))
        {
            if (FindAnchorable(d.Id) is { } hidden)
                HidePage(hidden);
        }

        foreach (var pane in PagePanes().ToList())
        {
            var pages = PagesIn(pane).ToList();
            if (pages.Count > 1)
                EvictAllBut(pane, pages[0]);
        }

        foreach (var d in _descriptors.Where(d => d.DefaultSide == DockSide.Tab))
        {
            var anchorable = CreateAnchorable(d);
            PlaceAtSide(anchorable, DockSide.Tab, d.DefaultRatio, d.DefaultTabTarget, takeSeat: false);
            if (!d.DefaultVisible)
                HidePage(anchorable);
        }

        root.CollectGarbage();
    }

    private string SerializeLayout()
        => DockLayoutSnapshotCodec.Serialize(CaptureLayoutSnapshot(_manager.Layout));

    /// <summary>布局加载后补齐缺失的已注册窗口(旧布局文件兼容)。跟随别页位置的页排在后面。</summary>
    private void EnsureRegisteredWindows()
    {
        var missing = _descriptors
            .Where(d => FindAnchorable(d.Id) == null)
            .OrderBy(d => d.DefaultSide == DockSide.Tab)
            .ToArray();
        foreach (var descriptor in missing)
            EnsureRegisteredWindow(descriptor);
    }

    private void ApplyPlacementFallback()
    {
        foreach (var descriptor in _descriptors)
        {
            if (!_orphanPlacements.TryGetValue(descriptor.Id, out var placement))
                continue;
            try
            {
                PlaceFromFallback(descriptor, placement);
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"窗口 {descriptor.Id} 的位置台账无效,已保留默认位置: {ex.Message}");
            }
        }

        foreach (var descriptor in _descriptors)
        {
            if (_orphanPlacements.TryGetValue(descriptor.Id, out var placement) &&
                placement.Hidden &&
                FindAnchorable(descriptor.Id) is { } anchorable)
            {
                HidePage(anchorable);
            }
        }

        _manager.Layout.CollectGarbage();
    }

    private void PlaceFromFallback(
        ToolWindowDescriptor descriptor,
        DockPlacementSnapshot placement)
    {
        var side = Enum.IsDefined(placement.Side) ? placement.Side : descriptor.DefaultSide;
        var target = placement.TabTarget;
        if (side == DockSide.Tab && string.IsNullOrWhiteSpace(target))
        {
            side = descriptor.DefaultSide;
            target = descriptor.DefaultTabTarget;
        }

        PlaceAtSide(MoveToAnchorable(descriptor), side, placement.Ratio, target, takeSeat: !placement.Hidden);
    }

    /// <summary>快照里没有的页按位置台账或默认位置补进来，一律不抢位。</summary>
    private void EnsureRegisteredWindow(ToolWindowDescriptor d)
    {
        if (FindAnchorable(d.Id) != null)
            return;

        var placement = _orphanPlacements.GetValueOrDefault(d.Id);
        var hidden = placement?.Hidden ?? (_hiddenCenterIds.Contains(d.Id) || !d.DefaultVisible);
        var anchorable = CreateAnchorable(d);
        PlaceAtSide(
            anchorable,
            placement?.Side ?? d.DefaultSide,
            placement?.Ratio ?? d.DefaultRatio,
            placement?.TabTarget ?? d.DefaultTabTarget,
            takeSeat: false);
        if (hidden)
            HidePage(anchorable);
        _preserveDefaultRatioOnSeed.Add(d.Id);
    }

    private bool LayoutHasMainDocumentPane() => TryFindMainDocumentPane() != null;

    private LayoutAnchorable CreateAnchorable(ToolWindowDescriptor d) => new()
    {
        ContentId = d.Id,
        Title = d.Title,
        Content = GetOrCreateContent(d),
        // §4.1:关闭按钮语义为隐藏,不销毁
        CanClose = false,
        CanHide = true,
        CanAutoHide = true,
        CanFloat = true,
        CanDockAsTabbedDocument = true,
    };

    /// <summary>
    /// 中央主文档区 = 根面板里第一个不在浮窗内的 <see cref="LayoutDocumentPane"/>。
    /// 浮出一个中央页时 AvalonDock 会为浮窗另建一个文档区,把中央区拖成左右两半也会分裂出第二个,
    /// 所以"整棵布局有且只有一个文档区"不成立 —— 取主文档区一律走这里,不得再用
    /// <c>Single</c>/<c>SingleOrDefault</c>(否则 <c>Sequence contains more than one element</c>
    /// 会从布局差分的定时器里以未处理异常的形式抛出来)。
    /// </summary>
    private LayoutDocumentPane? TryFindMainDocumentPane()
        => _manager.Layout.RootPanel.Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault(pane => !IsInsideFloatingWindow(pane));

    private LayoutDocumentPane FindMainDocumentPane()
        => TryFindMainDocumentPane()
           ?? throw new InvalidOperationException("布局中找不到中央主文档区");

    private static void NormalizeMainDocumentSizing(LayoutDocumentPane pane)
    {
        pane.DockWidth = Star(1);
        pane.DockHeight = Star(1);

        ILayoutElement current = pane;
        while (current.Parent is LayoutPanel panel && panel.Children.Count == 1)
        {
            SetDockLength(current, panel.Orientation == Orientation.Horizontal, Star(1));
            current = panel;
        }
    }

    private LayoutAnchorable MoveToAnchorable(ToolWindowDescriptor descriptor)
        => FindAnchorable(descriptor.Id) ?? CreateAnchorable(descriptor);

    private void ScheduleCenterDocumentPresentation()
    {
        if (_presentationRefreshPending)
            return;
        _presentationRefreshPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _presentationRefreshPending = false;
            RepairDocumentPaneSelection();
        });
    }

    /// <summary>
    /// 非空文档区必须有选中内容。窗格控件是 <c>TabControl</c>,模板里的
    /// <c>PART_SelectedContentHost</c> 绑的是 <c>SelectedContent</c>——
    /// <c>SelectedContentIndex</c> 停在 -1 时内容整片空白,
    /// 而这正是拖拽中途失败留下的残局(AvalonDock 新建的文档区不会自己选一页)。
    /// </summary>
    private void RepairDocumentPaneSelection()
    {
        var panes = _manager.Layout.Descendents()
            .OfType<LayoutDocumentPane>()
            .Where(pane => pane.Children.Count > 0)
            .ToList();
        if (panes.Count == 0)
            return;

        using (Suppress())
        {
            foreach (var pane in panes)
            {
                if (pane.SelectedContentIndex < 0 || pane.SelectedContentIndex >= pane.Children.Count)
                    pane.SelectedContentIndex = 0;

                SyncPaneControlSelection(pane);
            }
        }
    }

    /// <summary>
    /// 窗格控件要跟上模型的选中页（REQ-UI-083）。
    ///
    /// 模型改对了不等于屏幕上换了页：`LayoutDocumentPaneControl` 是个 `TabControl`，
    /// 它跟随模型靠的是 `SelectedContentIndex` 的属性变更通知，而中央区里
    /// **换页的那一跳会先经过一个 -1**（被取消选中的那一页会把窗格的下标写成 -1，
    /// 见 `CenterDocumentPane`）。控件看到 -1 时无页可选，等到最终值再来时
    /// 它有可能已经不跟了。因此在同一轮呈现里把控件按模型对齐一次。
    /// </summary>
    private void SyncPaneControlSelection(LayoutDocumentPane pane)
    {
        if (pane.SelectedContent is not { } selected)
            return;
        if (FindControlFor(pane) is not System.Windows.Controls.Primitives.Selector control)
            return;
        if (ReferenceEquals(control.SelectedItem, selected))
            return;

        control.SelectedItem = selected;
    }

    /// <summary>这一页还不在布局里（聚焦期间登记、布局恢复遗漏）：按默认位置补进来，不抢位。</summary>
    private void EnsureRegistered(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (FindAnchorable(id) != null)
            return;

        PlaceAtSide(
            CreateAnchorable(descriptor),
            descriptor.DefaultSide,
            descriptor.DefaultRatio,
            descriptor.DefaultTabTarget,
            takeSeat: false);
    }

    private object GetOrCreateContent(ToolWindowDescriptor d)
    {
        if (!_contents.TryGetValue(d.Id, out var content))
        {
            content = d.ContentFactory?.Invoke()
                      ?? throw new InvalidOperationException(
                          $"窗口 {d.Id} 未提供内容工厂(仅 console 由 Shell 接管内容)");
            _contents.Add(d.Id, content);
        }

        return content;
    }

    internal object? FindContent(string id)
        => _contents.GetValueOrDefault(id);

    private void BuildMaximizedLayout(string id)
    {
        _hiddenCenterIds.Clear();
        var pane = new LayoutAnchorablePane(CreateAnchorable(_byId[id]));
        var rootPanel = new LayoutPanel(pane) { Orientation = Orientation.Horizontal };
        _manager.Layout = new LayoutRoot { RootPanel = rootPanel };
    }

    private void TryDispose(object content, string id)
    {
        if (content is not IDisposable disposable)
            return;
        try
        {
            disposable.Dispose();
        }

        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"释放界面内容 {id} 失败: {ex.Message}");
        }
    }

    private void LoadOrphanPlacements()
    {
        var json = _settings?.Get(PlacementSettingsKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            _orphanPlacements = JsonSerializer.Deserialize<Dictionary<string, DockPlacementSnapshot>>(json)
                                ?? new Dictionary<string, DockPlacementSnapshot>(StringComparer.OrdinalIgnoreCase);
            _orphanPlacements = new Dictionary<string, DockPlacementSnapshot>(
                _orphanPlacements, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, placement) in _orphanPlacements)
                _lastVisiblePlacements[id] = placement;
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"读取延迟窗口位置失败: {ex.Message}");
            _orphanPlacements.Clear();
        }
    }

    /// <summary>每页的位置台账。一格一页之后没有「标签组目标」与「中央区第几页」，这两项一律写空。</summary>
    private Dictionary<string, DockPlacementSnapshot> CapturePlacements()
    {
        var placements = new Dictionary<string, DockPlacementSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _descriptors)
        {
            var state = ComputeState(descriptor.Id);
            var current = new DockPlacementSnapshot(
                state.Side ?? descriptor.DefaultSide,
                state.Ratio > 0 ? state.Ratio : _ratios.GetValueOrDefault(descriptor.Id, descriptor.DefaultRatio),
                !state.Visible,
                null);
            if (!state.Visible && _lastVisiblePlacements.TryGetValue(descriptor.Id, out var previous))
                current = previous with { Hidden = true };
            else if (state.Visible)
                _lastVisiblePlacements[descriptor.Id] = current;
            placements[descriptor.Id] = current;
        }
        return placements;
    }

    private void RememberCurrentPlacement(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            return;
        var placements = CapturePlacements();
        if (placements.TryGetValue(descriptor.Id, out var placement) && !placement.Hidden)
            _lastVisiblePlacements[id] = placement;
    }

    private void SavePlacements()
    {
        if (_settings == null)
            return;
        var placements = CapturePlacements();
        _settings.Set(PlacementSettingsKey, JsonSerializer.Serialize(placements));
    }

    private DockPlacementSnapshot? TakeOrphanPlacement(string id)
    {
        if (!_orphanPlacements.Remove(id, out var placement))
            return null;
        return placement;
    }


    // ---------------------------------------------------------------- 放置与比例

    /// <summary>
    /// 把一页放到某个方位（REQ-UI-100 一格一页）。那个方位已经有一格时放进那一格：
    /// <paramref name="takeSeat"/> 为 true 顶掉原来那一页，为 false（登记、恢复、摆回默认位置）新来的藏着。
    /// 同一侧不会为了多放一页再切出一块侧栏。
    ///
    /// <see cref="DockSide.Tab"/> 只来自页面声明（Janus 的「图」声明 <c>side=tab tabTarget=console</c>）：
    /// 1.20.2 起没有标签组，它的意思是「与目标页同一个位置」——目标此刻停在哪一格就进哪一格，
    /// 目标没露面就按目标的默认方位。
    /// </summary>
    private void PlaceAtSide(LayoutAnchorable a, DockSide side, double ratio, string? targetId, bool takeSeat)
    {
        var root = _manager.Layout;
        ToolWindowDescriptor? descriptor = null;
        if (a.ContentId != null)
            _byId.TryGetValue(a.ContentId, out descriptor);
        var fallbackRatio = descriptor?.DefaultRatio ?? 0.25;
        ratio = NormalizeRatio(ratio, fallbackRatio);

        if (side == DockSide.Tab)
        {
            var target = targetId != null ? FindAnchorable(targetId) : null;
            if (target is { IsHidden: false } &&
                !ReferenceEquals(target, a) &&
                !IsFloating(target) &&
                target.Parent is LayoutAnchorablePane or LayoutDocumentPane)
            {
                PutInPane((ILayoutContainer)target.Parent, a, takeSeat);
                root.CollectGarbage();
                return;
            }

            // 目标藏着（常见的是刚被顶掉）：它的位置是它上次露面的方位——隐藏前记下的那一条，不是它的默认方位。
            // 1.20.2 首版只认露着的目标，台账恢复时跟随页于是落到目标的默认方位去了。
            if (targetId != null &&
                _lastVisiblePlacements.TryGetValue(targetId, out var last) &&
                Enum.IsDefined(last.Side) && last.Side != DockSide.Tab)
            {
                side = last.Side;
            }
            else
            {
                side = targetId != null && _byId.TryGetValue(targetId, out var targetDescriptor) &&
                       targetDescriptor.DefaultSide != DockSide.Tab
                    ? targetDescriptor.DefaultSide
                    : DockSide.Right;
            }

            _log.Info(LayoutSource, $"窗口 {a.ContentId} 跟随的 {targetId ?? "(空)"} 此刻不在任何一格，按它的方位 {SideText(side)} 放");
        }

        if (side == DockSide.Center)
        {
            if (descriptor == null)
                throw new InvalidOperationException("未注册的窗口不能进入中央主区");
            var main = FindMainDocumentPane();
            PutInPane(main, a, takeSeat);
            a.CanDockAsTabbedDocument = true;
            NormalizeMainDocumentSizing(main);
            ScheduleCenterDocumentPresentation();
            root.CollectGarbage();
            return;
        }

        var existingPane = FindSidePane(side, a);
        if (existingPane != null)
        {
            PutInPane(existingPane, a, takeSeat);
            if (a.ContentId != null)
                _ratios[a.ContentId] = ratio;
            root.CollectGarbage();
            return;
        }

        Detach(a);
        var pane = new LayoutAnchorablePane(a);
        a.CanAutoHide = true;

        switch (side)
        {
            case DockSide.Left:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                EnsureSideRootPanel().Children.Insert(0, pane);
                break;

            case DockSide.Right:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                EnsureSideRootPanel().Children.Add(pane);
                break;

            case DockSide.Top:
            case DockSide.Bottom:
                {
                    var column = EnsureCenterColumn();
                    pane.DockHeight = DockLengthFor(horizontal: false, ratio);
                    if (side == DockSide.Top)
                        column.Children.Insert(0, pane);
                    else
                        column.Children.Add(pane);
                    break;
                }

        }

        if (a.ContentId != null)
            _ratios[a.ContentId] = ratio;
        root.CollectGarbage();
    }

    private LayoutAnchorablePane? FindSidePane(DockSide side, LayoutAnchorable excluded)
        => _manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Where(item => !ReferenceEquals(item, excluded)
                           && item.Parent is LayoutAnchorablePane
                           && !item.IsHidden
                           && !IsFloating(item))
            .FirstOrDefault(item => DetectSide(item) == side)
            ?.Parent as LayoutAnchorablePane;

    /// <summary>找到包含主文档区的中央列;若中央区不是垂直面板,则就地包一层。</summary>
    private LayoutPanel EnsureCenterColumn()
    {
        var document = FindMainDocumentPane();
        for (ILayoutContainer? parent = document.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutPanel { Orientation: Orientation.Vertical } vertical)
                return vertical;
        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel)
                     ?? throw new InvalidOperationException("布局中找不到中央主文档区");

        if (center is LayoutPanel { Orientation: Orientation.Vertical } column)
            return column;

        var idx = rootPanel.Children.IndexOf(center);
        rootPanel.Children.RemoveAt(idx);
        var wrap = new LayoutPanel { Orientation = Orientation.Vertical };
        wrap.DockWidth = GetDockLength(center, horizontal: true);
        wrap.Children.Add(center);
        rootPanel.Children.Insert(idx, wrap);
        return wrap;
    }

    private LayoutPanel EnsureSideRootPanel()
    {
        var column = EnsureCenterColumn();
        if (column.Parent is LayoutPanel { Orientation: Orientation.Horizontal } horizontal)
            return horizontal;

        var rootPanel = _manager.Layout.RootPanel;
        var center = ChildContaining(rootPanel, column)
                     ?? throw new InvalidOperationException("布局中找不到中央列");
        if (rootPanel.Orientation == Orientation.Horizontal)
            return rootPanel;

        var index = rootPanel.Children.IndexOf(center);
        rootPanel.Children.RemoveAt(index);
        var wrap = new LayoutPanel { Orientation = Orientation.Horizontal };
        wrap.DockHeight = GetDockLength(center, horizontal: false);
        wrap.Children.Add(center);
        rootPanel.Children.Insert(index, wrap);
        return wrap;
    }

    private bool NeedsCentralWorkspaceRepair()
    {
        if (_maximizedId != null)
            return false;

        if (HasStockDocumentPane())
            return true;

        // 1.20.0 起命令集不再是锚点（REQ-UI-095）：它藏了、浮了都不算坏，只有主文档区本身没了才修。
        return TryFindMainDocumentPane() == null;
    }

    private bool HasStockDocumentPane()
        => _manager.Layout.RootPanel.Descendents()
            .OfType<LayoutDocumentPane>()
            .Any(pane => pane is not CenterDocumentPane && !IsInsideFloatingWindow(pane));

    /// <summary>
    /// 中央文档窗格必须是 <see cref="CenterDocumentPane"/>（REQ-UI-083）。
    ///
    /// Aurora 自己建的都是，但**AvalonDock 也会建**：把一页浮出去再丢回中央区、
    /// 或者把中央区拖成左右两半，新出来的那个窗格是原装的 <see cref="LayoutDocumentPane"/>，
    /// 它的 <c>IndexOf</c> 不认工具页——于是「换不了页、拖不动」在那一格里当场复发，
    /// 而且只在拖过的那台机器上复发，是最难被发现的那一类回归。
    ///
    /// 因此在布局差分这一层把它换掉：孩子、选中页与尺寸原样搬过去，用户看不出发生过什么。
    /// </summary>
    private bool UpgradeStockDocumentPanes()
    {
        var stock = _manager.Layout.RootPanel.Descendents()
            .OfType<LayoutDocumentPane>()
            .Where(pane => pane is not CenterDocumentPane && !IsInsideFloatingWindow(pane))
            .ToList();
        if (stock.Count == 0)
            return false;

        foreach (var pane in stock)
        {
            if (pane.Parent is not ILayoutContainer container)
                continue;

            var replacement = new CenterDocumentPane
            {
                ShowHeader = pane.ShowHeader,
                DockWidth = pane.DockWidth,
                DockHeight = pane.DockHeight,
                DockMinWidth = pane.DockMinWidth,
                DockMinHeight = pane.DockMinHeight,
            };
            // 原装窗格的选中下标多半已经被那个 -1 写坏了，而**被点的那一页自己**
            // 的 IsSelected 仍是 true（回写发生在它之后）。因此选中页先看窗格，
            // 再退回去问内容——否则升级完这一格会莫名其妙跳回第一页。
            var selected = pane.SelectedContent
                           ?? pane.Children.FirstOrDefault(child => child.IsSelected);
            foreach (var child in pane.Children.ToList())
            {
                pane.Children.Remove(child);
                replacement.Children.Add(child);
            }

            container.ReplaceChild(pane, replacement);
            if (selected != null)
                replacement.SelectedContentIndex = replacement.Children.IndexOf(selected);
        }

        _manager.Layout.CollectGarbage();
        _log.Info(LayoutSource, $"{stock.Count} 个中央文档窗格已换成认得工具页下标的实现");
        return true;
    }

    /// <summary>
    /// 主文档区必须在，且必须是 <see cref="CenterDocumentPane"/>。
    ///
    /// 1.19.0 及以前这里还要求命令集钉在主文档区里（藏掉、浮出都会被当场放回），
    /// 因为窗口按钮栏挂在那个窗格的页签行上。1.20.0 按钮栏搬去右栏，命令集解除锚点
    /// （REQ-UI-095，DEC-031 的替代条件成立）：主文档区可以是空的，里面放哪一页都行。
    /// </summary>
    private bool EnsureCentralWorkspace()
    {
        if (_maximizedId != null)
            return false;

        var upgraded = UpgradeStockDocumentPanes();

        var pane = TryFindMainDocumentPane();
        if (pane == null)
        {
            // AvalonDock 自己会留住最后一个文档区；走到这里说明布局树被外力改坏了，补一个空的。
            var rootPanel = _manager.Layout.RootPanel;
            pane = new CenterDocumentPane();
            rootPanel.Children.Insert(rootPanel.Children.Count / 2, pane);
            _log.Info(LayoutSource, "主文档区缺失，已补一个空的");
            upgraded = true;
        }

        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
        return upgraded;
    }

    private void ScheduleCentralWorkspaceRepair()
    {
        if (_centerRepairPending || _suppress > 0)
            return;
        _centerRepairPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _centerRepairPending = false;
            if (_suppress > 0 || !NeedsCentralWorkspaceRepair())
                return;
            using (Suppress())
                EnsureCentralWorkspace();
        });
    }

    private static ILayoutPanelElement? FindCenterChild(LayoutPanel rootPanel)
        => rootPanel.Children.FirstOrDefault(c =>
            c is LayoutDocumentPane ||
            c.Descendents().OfType<LayoutDocumentPane>().Any());

    private static bool IsHostedInDocumentPane(LayoutAnchorable anchorable)
    {
        for (ILayoutContainer? parent = anchorable.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutDocumentPane)
                return true;
        }

        return false;
    }

    private void Detach(LayoutAnchorable a)
    {
        if (a.IsHidden)
        {
            _manager.Layout.Hidden.Remove(a);
            return;
        }

        if (a.Parent is ILayoutContainer container)
            container.RemoveChild(a);
    }

    private void ApplyRatio(LayoutAnchorable a, DockSide side, double ratio)
    {
        if (side == DockSide.Center)
            return;
        var horizontal = side is DockSide.Left or DockSide.Right;
        var panel = horizontal
            ? _manager.Layout.RootPanel
            : EnsureCenterColumn();

        var child = ChildContaining(panel, a);
        if (child == null)
        {
            _log.Warn(LayoutSource, "未能定位窗口所在的布局分区,比例未调整");
            return;
        }

        SetDockLength(child, horizontal, DockLengthFor(horizontal, ratio));
        foreach (var item in child.Descendents().OfType<LayoutAnchorable>())
        {
            if (item.ContentId != null && _byId.ContainsKey(item.ContentId))
                _ratios[item.ContentId] = ratio;
        }
    }

    /// <summary>
    /// 目标尺寸的表达:主窗体已量得实际尺寸时用像素(AvalonDock 对侧窗格
    /// 的原生语义),否则先用星值占位,待首次排布后由 ReapplyRatios 修正。
    /// </summary>
    private GridLength DockLengthFor(bool horizontal, double ratio)
    {
        var total = horizontal ? _manager.ActualWidth : _manager.ActualHeight;
        return total > 0
            ? new GridLength(Math.Max(ratio * total, 25), GridUnitType.Pixel)
            : Star(ratio);
    }

    private void ScheduleReapplyRatios()
        => _manager.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                using (Suppress())
                {
                    ReapplyRatios();
                }
            });

    /// <summary>
    /// 按记录的“占主程序窗体百分比”重设各停靠分区尺寸(W-05)。
    /// 恢复布局后的首次调用改为反向采集:以布局文件里的尺寸为准更新比例记录。
    /// </summary>
    private void ReapplyRatios()
    {
        if (_maximizedId != null)
            return;
        if (_manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return;

        if (_seedRatiosFromLayout)
        {
            _seedRatiosFromLayout = false;
            var addedAfterSavedLayout = _preserveDefaultRatioOnSeed.ToArray();
            foreach (var d in _descriptors)
            {
                if (_preserveDefaultRatioOnSeed.Contains(d.Id))
                    continue;
                var s = ComputeState(d.Id);
                if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab, Ratio: > 0 })
                    _ratios[d.Id] = s.Ratio;
            }

            _preserveDefaultRatioOnSeed.Clear();

            // Restored pixel panes and newly inserted star panes otherwise share incompatible
            // sizing semantics. Reapply each new descriptor's declared ratio after layout has
            // an actual size so it cannot collapse to 1-3%.
            foreach (var id in addedAfterSavedLayout)
            {
                var anchorable = FindAnchorable(id);
                var side = anchorable == null ? null : DetectSide(anchorable);
                if (anchorable != null && side is not null and not DockSide.Tab and not DockSide.Center)
                    ApplyRatio(anchorable, side.Value, _ratios[id]);
            }

        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel);
        if (center == null)
            return;

        var rootHorizontal = rootPanel.Orientation == Orientation.Horizontal;
        ReapplyPanelRatios(rootPanel, center, rootHorizontal);

        if (center is LayoutPanel column)
        {
            var innerDoc = column.Children.FirstOrDefault(c =>
                c is LayoutDocumentPane || c.Descendents().OfType<LayoutDocumentPane>().Any());
            var columnHorizontal = column.Orientation == Orientation.Horizontal;
            if (innerDoc != null)
                ReapplyPanelRatios(column, innerDoc, columnHorizontal);
        }
    }

    private void ReapplyPanelRatios(
        LayoutPanel panel,
        ILayoutPanelElement center,
        bool horizontal)
    {
        var sides = panel.Children
            .Where(child => !ReferenceEquals(child, center))
            .Select(child => (Child: child, Ratio: RatioOfSubtree(child)))
            .Where(item => item.Ratio is > 0)
            .Select(item => (item.Child, Ratio: item.Ratio!.Value))
            .ToList();
        var requested = sides.Sum(item => item.Ratio);
        var scale = requested > MaximumSideAllocation
            ? MaximumSideAllocation / requested
            : 1d;

        foreach (var (child, ratio) in sides)
        {
            var effective = ratio * scale;
            SetDockLength(child, horizontal, DockLengthFor(horizontal, effective));
            foreach (var anchorable in child.Descendents().OfType<LayoutAnchorable>())
            {
                if (anchorable.ContentId != null && _byId.ContainsKey(anchorable.ContentId))
                    _ratios[anchorable.ContentId] = effective;
            }
        }
    }

    /// <summary>分区内第一个已注册窗口的目标比例(分区尺寸由其代表)。</summary>
    private double? RatioOfSubtree(ILayoutPanelElement subtree)
    {
        var anchorables = subtree is LayoutAnchorable self
            ? new[] { self }.AsEnumerable()
            : subtree.Descendents().OfType<LayoutAnchorable>();
        foreach (var a in anchorables)
        {
            if (a.ContentId != null && _ratios.TryGetValue(a.ContentId, out var r))
                return r;
        }

        return null;
    }


    // ---------------------------------------------------------------- 一格一页（REQ-UI-100）
    //
    // 侧边、底边、主文档区、浮窗里的每一格窗格，任何时刻只放一页。
    //
    // 1.20.0 / 1.20.1 的做法是「先让页挤进同一格，再挑一页留下、其余隐藏」：登记时新来的藏着等位
    // （等位账），用户拖进来时比对上一次记下的「每页在哪一格」认新来的，场景切换时再按偏好挑。
    // 真机上它会在几页之间来回抢位——一格里允许存在多页，只是只显示一页。1.20.2 把这整条链删掉：
    //
    //   * 程序路径放一页进一格，先把这一格原来那一页藏起来，再放进去（PutInPane）。
    //     「不抢位」的放法（登记、恢复、摆回默认位置）是这一格有页时新来的直接藏起来，不记账、不等位；
    //   * 唯一绕不开的是 AvalonDock 自己的「放进这一格」停靠点：它把拖来的页插成同一格的第二页。
    //     布局一变就收拾（EvictExtraPages）：留刚从浮窗落下来的那一页。
    //
    // 没有页签、没有页签切换：被顶掉的页只是隐藏，要回来走右栏常用页面或 aurora.ui.show。

    private bool _singlePagePending;

    /// <summary>
    /// 此刻浮着的页（含拖动途中刚建出来的浮窗里的页）。用户把浮窗落进一格时，新来的就是它。
    /// 只是「最近一次看到的浮窗内容」，每次收拾完、每次重建基线都按布局树重取，不跨布局累积。
    /// </summary>
    private HashSet<string> _floatingPages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把一页放进一格。<paramref name="takeSeat"/> 为 true 时这一格原来那一页被藏起来；
    /// 为 false 时（不抢位）这一格已经有页，新来的直接藏起来——AvalonDock 记住的「上一个容器」
    /// 就是这一格，之后显示它，它回到这里并顶掉当时那一页。
    /// </summary>
    /// <returns>这一页此刻露面。</returns>
    private bool PutInPane(ILayoutContainer pane, LayoutAnchorable page, bool takeSeat)
    {
        var occupied = PagesIn(pane).Any(other => !ReferenceEquals(other, page));
        if (occupied && takeSeat)
            EvictAllBut(pane, page);

        if (!ReferenceEquals(page.Parent, pane))
        {
            Detach(page);
            switch (pane)
            {
                case LayoutAnchorablePane anchorablePane:
                    anchorablePane.Children.Add(page);
                    break;
                case LayoutDocumentPane documentPane:
                    documentPane.Children.Add(page);
                    break;
                default:
                    throw new InvalidOperationException($"不支持的窗格类型: {pane.GetType().Name}");
            }
        }

        page.CanAutoHide = true;
        if (occupied && !takeSeat)
        {
            HidePage(page);
            return false;
        }

        _hiddenCenterIds.Remove(page.ContentId ?? string.Empty);
        page.IsSelected = true;
        return true;
    }

    /// <summary>这一格里除 <paramref name="keep"/> 以外的页一律藏起来。</summary>
    private void EvictAllBut(ILayoutContainer pane, LayoutContent keep)
    {
        foreach (var other in PagesIn(pane).Where(page => !ReferenceEquals(page, keep)).ToList())
        {
            HidePage(other);
            _log.Info(LayoutSource, $"一格一页：{keep.ContentId} 进来，{other.ContentId} 已隐藏");
        }
    }

    /// <summary>藏起一页并记住它的位置。被顶掉与被明确隐藏走同一条路，没有「等位」这回事。</summary>
    private void HidePage(LayoutAnchorable page)
    {
        if (page.IsHidden)
            return;
        var id = page.ContentId!;
        RememberCurrentPlacement(id);
        var wasCenter = IsHostedInDocumentPane(page);
        page.Hide();
        if (wasCenter)
            _hiddenCenterIds.Add(id);
    }

    /// <summary>一格里已登记的页。</summary>
    private IEnumerable<LayoutAnchorable> PagesIn(ILayoutContainer pane)
        => pane.Children
            .OfType<LayoutAnchorable>()
            .Where(page => page.ContentId is { } id && _byId.ContainsKey(id));

    /// <summary>主文档区、侧边与底边窗格、浮窗里的窗格。自动隐藏的边条不算格子。</summary>
    private IEnumerable<ILayoutContainer> PagePanes()
        => _manager.Layout.Descendents()
            .Where(element => element is LayoutAnchorablePane or LayoutDocumentPane)
            .Cast<ILayoutContainer>();

    /// <summary>
    /// 布局里出现了一格多页（AvalonDock 的「放进这一格」停靠点，或 1.20.1 及以前存下的布局）：
    /// 每格留一页，其余隐藏。留哪一页按先后：<paramref name="keepId"/>；刚从浮窗落下来的；
    /// 活动页；这一格的选中页；最后一页。
    /// 专注态换的是另一棵布局树，不处理。
    /// </summary>
    internal void EvictExtraPages(string? keepId = null)
    {
        if (_maximizedId != null)
            return;

        var active = _manager.Layout.ActiveContent;
        using (Suppress())
        {
            foreach (var pane in PagePanes().ToList())
            {
                var pages = PagesIn(pane).ToList();
                if (pages.Count <= 1)
                    continue;

                var keep = pages.FirstOrDefault(page =>
                               string.Equals(page.ContentId, keepId, StringComparison.OrdinalIgnoreCase))
                           ?? pages.LastOrDefault(page => _floatingPages.Contains(page.ContentId!))
                           ?? pages.FirstOrDefault(page => ReferenceEquals(page, active))
                           ?? pages.FirstOrDefault(page => page.IsSelected)
                           ?? pages[^1];
                EvictAllBut(pane, keep);
                if (keep.Parent != null)
                    keep.IsSelected = true;
            }
        }

        RecordFloatingPages();
    }

    private void RecordFloatingPages()
        => _floatingPages = _manager.Layout.FloatingWindows
            .SelectMany(window => window.Descendents().OfType<LayoutContent>())
            .Select(content => content.ContentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>拖动途中新建的浮窗：里面的页记为「浮着」，落进哪一格它就留在哪一格。</summary>
    private void OnFloatingWindowControlCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        foreach (var content in e.LayoutFloatingWindowControl.Model.Descendents().OfType<LayoutContent>())
        {
            if (!string.IsNullOrWhiteSpace(content.ContentId))
                _floatingPages.Add(content.ContentId);
        }
    }

    /// <summary>用户手势改了布局：等这一拍的布局事件都落定，再看哪一格里多了一页。</summary>
    private void ScheduleSinglePageCheck()
    {
        if (_singlePagePending)
            return;
        _singlePagePending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _singlePagePending = false;
            if (_maximizedId != null)
                return;
            if (_suppress > 0)
            {
                // 程序路径正在改布局：它自己不会留下一格多页；这一拍的用户手势等它放手再看。
                ScheduleSinglePageCheck();
                return;
            }

            EvictExtraPages();
        });
    }

    /// <summary>
    /// 拖出去没有落到停靠点的页（REQ-UI-098）：摆回它的默认位置并隐藏，不顶掉那里原来的页。
    ///
    /// 为什么不直接隐藏：AvalonDock 隐藏一页时记住的「上一个容器」是那个浮窗，
    /// 下次再显示它会回到一个已经关掉的浮窗里。先摆回默认位置，隐藏记住的就是停靠位。
    /// </summary>
    public void ParkHidden(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var anchorable = MoveToAnchorable(_byId[id]);
            PlaceAtDefault(anchorable, takeSeat: false);
            if (!anchorable.IsHidden)
                HidePage(anchorable);
            _manager.Layout.CollectGarbage();
            EnsureCentralWorkspace();
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按登记时的默认位置放一页。</summary>
    private void PlaceAtDefault(LayoutAnchorable anchorable, bool takeSeat)
    {
        var descriptor = _byId[anchorable.ContentId!];
        PlaceAtSide(anchorable, descriptor.DefaultSide, descriptor.DefaultRatio, descriptor.DefaultTabTarget, takeSeat);
    }

    /// <summary>
    /// 让一个藏着的工具页露面：显示、浮出、自动隐藏之前都走这里。
    ///
    /// AvalonDock 按「上一个容器」放回它；那个容器可能已经不在布局树里了（主文档区被换成了新窗格、
    /// 浮窗关掉了）。还记着旧容器时放进去等于凭空消失，<c>CollectGarbage</c> 已把旧容器清掉时
    /// 它自作主张挂到侧边窗格上——所以露面之后不在树里、或者本是中央页却没回到文档区，都按默认位置重新落位。
    ///
    /// <paramref name="takeSeat"/> 为 false 时（浮出、自动隐藏）不顶掉任何一格：要重新落位就单开一格，
    /// 反正它马上就要离开这里。
    /// </summary>
    private void RevealAnchorable(LayoutAnchorable anchorable, string id, bool takeSeat)
    {
        var wasCenter = _hiddenCenterIds.Remove(id);
        if (anchorable.IsHidden)
            anchorable.Show();
        if (anchorable.Root != null && !(wasCenter && !IsHostedInDocumentPane(anchorable)))
        {
            if (takeSeat && anchorable.Parent is ILayoutContainer pane)
                EvictAllBut(pane, anchorable);
            return;
        }

        if (anchorable.Parent is ILayoutContainer lost)
            lost.RemoveChild(anchorable);

        _log.Info(LayoutSource, $"窗口 {id} 原来的容器已不在布局里，按默认位置放回");
        if (!takeSeat)
        {
            EnsureSideRootPanel().Children.Add(new LayoutAnchorablePane(anchorable));
            return;
        }

        if (wasCenter)
            PutInPane(FindMainDocumentPane(), anchorable, takeSeat: true);
        else
            PlaceAtDefault(anchorable, takeSeat: true);
    }
}
