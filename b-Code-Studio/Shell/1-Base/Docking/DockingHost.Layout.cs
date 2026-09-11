using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base.Docking;

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
}
