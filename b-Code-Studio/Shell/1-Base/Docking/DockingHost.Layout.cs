using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryAurora.Shell.Base.Docking;

internal sealed partial class DockingHost
{
    private void BuildDefaultLayout()
    {
        _preserveDefaultRatioOnSeed.Clear();
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        var docPane = new CenterDocumentPane();

        // 中央主区使用 AvalonDock 原生文档窗格。命令集保留文档身份；模块和消费方
        // 的 Center 窗口以工具窗口身份挂入同一主区，避免退役的文档页注册语义回流。
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

        foreach (var descriptor in _descriptors
                     .Where(d => d.DefaultSide == DockSide.Center)
                     .OrderByDescending(d => IsPrimaryCommandDocument(d.Id)))
        {
            if (UsesDocumentIdentity(descriptor))
            {
                var document = CreateDocument(descriptor);
                if (descriptor.DefaultVisible)
                    docPane.Children.Add(document);
                else
                    _hiddenCenterIds.Add(descriptor.Id);
            }
            else
            {
                var anchorable = CreateAnchorable(descriptor);
                if (descriptor.DefaultVisible)
                    docPane.Children.Add(anchorable);
                else
                    _hiddenCenterIds.Add(descriptor.Id);
            }
        }

        centerColumn.DockWidth = Star(Math.Max(1 - leftRatio - rightRatio, 0.1));
        docPane.DockHeight = Star(Math.Max(1 - topRatio - bottomRatio, 0.1));

        // 第二遍:并入标签组的窗口(DefaultSide = Tab)
        foreach (var d in _descriptors.Where(d => d.DefaultSide == DockSide.Tab))
        {
            var documentTarget = d.DefaultTabTarget != null
                ? FindCenterDocument(d.DefaultTabTarget)
                : null;
            if (documentTarget?.Parent is LayoutDocumentPane documentPane)
            {
                documentPane.Children.Add(CreateAnchorable(d));
                continue;
            }

            var a = CreateAnchorable(d);
            var target = d.DefaultTabTarget != null ? FindAnchorable(d.DefaultTabTarget) : null;
            if (target?.Parent is LayoutAnchorablePane tp)
            {
                tp.Children.Add(a);
            }
            else
            {
                _log.Warn(LayoutSource, $"窗口 {d.Id} 的默认标签组目标 {d.DefaultTabTarget ?? "(空)"} 不存在,改为右侧停靠");
                var pane = new LayoutAnchorablePane(a)
                {
                    DockWidth = Star(NormalizeRatio(d.DefaultRatio, 0.25)),
                };
                rootPanel.Children.Add(pane);
            }
        }

        // 默认隐藏的窗口
        foreach (var d in _descriptors.Where(d => !d.DefaultVisible))
            FindAnchorable(d.Id)?.Hide();

        root.CollectGarbage();
    }

    private string SerializeLayout()
        => DockLayoutSnapshotCodec.Serialize(CaptureLayoutSnapshot(_manager.Layout));

    /// <summary>布局加载后补齐缺失的已注册窗口(旧布局文件兼容)。</summary>
    private void EnsureRegisteredWindows()
    {
        var missing = _descriptors
            .Where(d => FindAnchorable(d.Id) == null && FindCenterDocument(d.Id) == null)
            .ToArray();

        // 先补齐中央文档，再处理 Tab 跟随页，避免恢复结果依赖描述符声明顺序。
        foreach (var descriptor in missing.Where(UsesDocumentIdentity))
            EnsureRegisteredWindow(descriptor);
        foreach (var descriptor in missing.Where(d => !UsesDocumentIdentity(d)))
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

        // 先让所有标签目标完成落位，再隐藏页面；否则隐藏的目标会暂时失去所属窗格。
        foreach (var descriptor in _descriptors)
        {
            if (!_orphanPlacements.TryGetValue(descriptor.Id, out var placement) ||
                !placement.Hidden)
            {
                continue;
            }

            var document = FindCenterDocument(descriptor.Id);
            if (document != null)
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(descriptor.Id);
            }
            else if (FindAnchorable(descriptor.Id) is { } anchorable)
            {
                var wasCenter = IsHostedInDocumentPane(anchorable);
                anchorable.Hide();
                if (wasCenter)
                    _hiddenCenterIds.Add(descriptor.Id);
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

        if (IsPrimaryCommandDocument(descriptor.Id) || side == DockSide.Center ||
            side == DockSide.Tab && target != null && IsCenterTabTarget(target))
        {
            if (UsesDocumentIdentity(descriptor))
            {
                ShowCenterDocument(
                    MoveToCenterDocument(descriptor),
                    placement.CenterIndex,
                    placement.Selected ?? false);
            }
            else
            {
                ShowAnchorableAsCenterPage(
                    MoveToAnchorable(descriptor),
                    placement.CenterIndex,
                    placement.Selected ?? false);
            }
            return;
        }

        var anchorable = MoveToAnchorable(descriptor);
        PlaceAtSide(anchorable, side, placement.Ratio, target);
        if (placement.Selected == true)
            anchorable.IsSelected = true;
    }

    private void EnsureRegisteredWindow(ToolWindowDescriptor d)
    {
        if (FindAnchorable(d.Id) != null || FindCenterDocument(d.Id) != null)
            return;

        if (UsesDocumentIdentity(d))
        {
            var placement = _orphanPlacements.GetValueOrDefault(d.Id);
            var hidden = _hiddenCenterIds.Contains(d.Id)
                         || !d.DefaultVisible
                         || placement?.Hidden == true;
            var document = MoveToCenterDocument(d);
            if (hidden)
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(d.Id);
            }
            else
            {
                ShowCenterDocument(
                    document,
                    placement?.CenterIndex,
                    placement?.Selected ?? false);
            }
        }
        else if (d.DefaultSide == DockSide.Center
                 || (d.DefaultSide == DockSide.Tab && IsCenterTabTarget(d.DefaultTabTarget)))
        {
            // 中央区的工具窗口：位置仍是中央，身份是 LayoutAnchorable。
            // 隐藏态与落位沿用中央页的既有语义，使它与此前的文档页在用户可见行为上一致。
            var placement = _orphanPlacements.GetValueOrDefault(d.Id);
            var hidden = _hiddenCenterIds.Contains(d.Id)
                         || !d.DefaultVisible
                         || placement?.Hidden == true;
            var anchorable = MoveToAnchorable(d);
            ShowAnchorableAsCenterPage(
                anchorable,
                placement?.CenterIndex,
                placement?.Selected ?? false);
            if (hidden)
            {
                anchorable.Hide();
                _hiddenCenterIds.Add(d.Id);
            }
        }
        else
        {
            var placement = _orphanPlacements.GetValueOrDefault(d.Id);
            var side = placement?.Side ?? d.DefaultSide;
            var target = placement?.TabTarget ?? d.DefaultTabTarget;
            var anchorable = CreateAnchorable(d);
            var targetPending = side == DockSide.Tab && target != null &&
                                !IsCenterTabTarget(target) &&
                                FindAnchorable(target)?.Parent is not LayoutAnchorablePane;
            if (targetPending)
            {
                _pendingTabTargets[d.Id] = target!;
                _log.Info(LayoutSource, $"窗口 {d.Id} 的标签组目标 {target} 尚未注册，暂时右侧停靠");
            }
            PlaceAtSide(
                anchorable,
                targetPending ? DockSide.Right : side,
                placement?.Ratio ?? d.DefaultRatio,
                targetPending ? null : target);
            if (placement?.Hidden ?? !d.DefaultVisible)
                anchorable.Hide();
        }
        _preserveDefaultRatioOnSeed.Add(d.Id);
        ResolvePendingTabTargets(d.Id);
    }

    private bool LayoutHasMainDocumentPane()
    {
        var pane = TryFindMainDocumentPane();
        return pane != null && pane.Children.OfType<LayoutDocument>().All(document =>
            document.ContentId != null && _byId.ContainsKey(document.ContentId));
    }

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

    private LayoutDocument CreateDocument(ToolWindowDescriptor descriptor)
    {
        var document = new LayoutDocument
        {
            ContentId = descriptor.Id,
            Title = descriptor.Title,
            Content = GetOrCreateContent(descriptor),
            CanClose = false,
            // 1.20.0 起命令集也能浮出（REQ-UI-095）；落不到停靠点就隐藏，见 REQ-UI-098。
            CanFloat = true,
        };
        _centerDocuments[descriptor.Id] = document;
        return document;
    }

    private bool IsPrimaryCommandDocument(string id)
        => id.Equals(StandardWindowIds.Mcp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 只有宿主自持的主命令页使用文档身份(<c>LayoutDocument</c>)；其余一律是工具窗口
    /// (<c>LayoutAnchorable</c>)，声明 <see cref="DockSide.Center"/> 的窗口仍落在中央区，
    /// 但以中央页形态呈现(<c>ShowAnchorableAsCenterPage</c>)。
    ///
    /// 为什么模块不再能注册文档页：文档页的拖动行为明显弱于工具窗口，且会引出一连串
    /// 停靠相关缺陷。与其逐个修复文档页的拖动路径，不如取消这条注册路径——**中央位置**
    /// 是模块真正需要的，**文档身份**只是当初取得该位置的手段。
    ///
    /// 旧布局自动迁移：恢复时若存档中的 LayoutDocument 对应的描述符已不再是文档身份，
    /// 反序列化回调会取消该节点，随后由 EnsureRegisteredWindow 以工具窗口重建。
    /// </summary>
    private bool UsesDocumentIdentity(ToolWindowDescriptor descriptor)
        => IsPrimaryCommandDocument(descriptor.Id);

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

    private LayoutDocument? FindCenterDocument(string id)
    {
        if (_centerDocuments.TryGetValue(id, out var cached))
            return cached;

        var document = _manager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(item => string.Equals(item.ContentId, id, StringComparison.OrdinalIgnoreCase));
        if (document != null)
            _centerDocuments[id] = document;
        return document;
    }

    private LayoutDocument MoveToCenterDocument(ToolWindowDescriptor descriptor)
    {
        var anchorable = FindAnchorable(descriptor.Id);
        if (anchorable != null)
        {
            Detach(anchorable);
            anchorable.Content = null;
        }

        var document = FindCenterDocument(descriptor.Id) ?? CreateDocument(descriptor);
        _hiddenCenterIds.Remove(descriptor.Id);
        return document;
    }

    private void ShowCenterDocument(LayoutDocument document, int? index = null, bool select = true)
    {
        var pane = FindMainDocumentPane();
        var previousSelection = pane.SelectedContent;
        if (!ReferenceEquals(document.Parent, pane))
        {
            DetachDocument(document);
            var targetIndex = Math.Clamp(index ?? pane.Children.Count, 0, pane.Children.Count);
            pane.Children.Insert(targetIndex, document);
        }
        _hiddenCenterIds.Remove(document.ContentId ?? string.Empty);
        if (select)
        {
            document.IsSelected = true;
            document.IsActive = true;
        }
        else if (previousSelection != null && !ReferenceEquals(previousSelection, document))
        {
            previousSelection.IsSelected = true;
        }
        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
    }

    private void ShowAnchorableAsCenterPage(LayoutAnchorable anchorable, int? index = null, bool select = true)
    {
        var pane = FindMainDocumentPane();
        var previousSelection = pane.SelectedContent;
        if (!ReferenceEquals(anchorable.Parent, pane))
        {
            Detach(anchorable);
            var targetIndex = Math.Clamp(index ?? pane.Children.Count, 0, pane.Children.Count);
            pane.Children.Insert(targetIndex, anchorable);
        }
        anchorable.CanDockAsTabbedDocument = true;
        if (select)
        {
            anchorable.IsSelected = true;
            anchorable.IsActive = true;
        }
        else if (previousSelection != null && !ReferenceEquals(previousSelection, anchorable))
        {
            previousSelection.IsSelected = true;
        }
        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
    }

    private bool IsCenterContent(string id)
        => FindCenterDocument(id)?.Parent is LayoutDocumentPane ||
           FindAnchorable(id) is { } anchorable && IsHostedInDocumentPane(anchorable);

    private bool IsCenterTabTarget(string? id)
        => id != null && IsCenterContent(id);

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
    {
        var document = FindCenterDocument(descriptor.Id);
        if (document != null)
        {
            DetachDocument(document);
            document.Content = null;
            _centerDocuments.Remove(descriptor.Id);
            _hiddenCenterIds.Remove(descriptor.Id);
        }
        return FindAnchorable(descriptor.Id) ?? CreateAnchorable(descriptor);
    }

    private static void DetachDocument(LayoutDocument document)
    {
        if (document.Parent is ILayoutContainer container)
            container.RemoveChild(document);
    }

    private void ScheduleCenterDocumentPresentation()
    {
        if (_presentationRefreshPending)
            return;
        _presentationRefreshPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _presentationRefreshPending = false;
            RepairDocumentPaneSelection();
            var mainPane = _manager.Layout.RootPanel.Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault(pane => !IsInsideFloatingWindow(pane));
            if (mainPane == null)
                return;

            foreach (var control in FindVisualDescendants(_manager).OfType<LayoutDocumentPaneControl>())
            {
                if (control is not ILayoutControl { Model: LayoutDocumentPane pane } ||
                    !ReferenceEquals(pane, mainPane))
                    continue;
                foreach (var tabs in FindVisualDescendants(control).OfType<DocumentPaneTabPanel>())
                    tabs.Visibility = Visibility.Visible;
            }
        });
    }

    /// <summary>
    /// 非空文档区必须有选中内容。窗格控件是 <c>TabControl</c>,模板里的
    /// <c>PART_SelectedContentHost</c> 绑的是 <c>SelectedContent</c>——
    /// <c>SelectedContentIndex</c> 停在 -1 时页签照画、内容整片空白,
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
    /// 它有可能已经不跟了——真机症状是 `aurora.ui.show` 报成功、快照里
    /// `selectedContentId` 也对，而屏幕上仍停在原来那一页（2026-09-07）。
    ///
    /// 因此在同一轮呈现里把控件按模型对齐一次。两边一致时什么都不做，
    /// 不会打断用户自己点出来的选中。
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

    private void EnsureRegistered(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (FindAnchorable(id) != null || FindCenterDocument(id) != null)
            return;

        if (UsesDocumentIdentity(descriptor))
        {
            ShowCenterDocument(MoveToCenterDocument(descriptor));
            return;
        }

        if (descriptor.DefaultSide == DockSide.Tab && descriptor.DefaultTabTarget != null &&
            FindCenterDocument(descriptor.DefaultTabTarget) != null)
        {
            ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
            return;
        }

        var anchorable = CreateAnchorable(descriptor);
        PlaceAtSide(anchorable, descriptor.DefaultSide, descriptor.DefaultRatio, descriptor.DefaultTabTarget);
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
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        ILayoutPanelElement pane = UsesDocumentIdentity(_byId[id])
            ? new LayoutDocumentPane(CreateDocument(_byId[id]))
            : new LayoutAnchorablePane(CreateAnchorable(_byId[id]));
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

    private Dictionary<string, DockPlacementSnapshot> CapturePlacements()
    {
        var placements = new Dictionary<string, DockPlacementSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _descriptors)
        {
            var state = ComputeState(descriptor.Id);
            LayoutContent? centerContent = FindCenterDocument(descriptor.Id);
            centerContent ??= FindAnchorable(descriptor.Id) is { } anchorable && IsHostedInDocumentPane(anchorable)
                ? anchorable
                : null;
            var centerPane = centerContent?.Parent as LayoutDocumentPane;
            var current = new DockPlacementSnapshot(
                state.Side ?? descriptor.DefaultSide,
                state.Ratio > 0 ? state.Ratio : _ratios.GetValueOrDefault(descriptor.Id, descriptor.DefaultRatio),
                !state.Visible,
                state.TabTarget,
                centerPane == null ? null : centerPane.Children.IndexOf(centerContent!),
                centerPane == null ? null : ReferenceEquals(centerPane.SelectedContent, centerContent));
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

    private void ApplyStoredCenterVisibility()
    {
        foreach (var (id, placement) in _orphanPlacements.ToArray())
        {
            var document = FindCenterDocument(id);
            if (document == null)
                continue;

            _orphanPlacements.Remove(id);
            if (placement.Hidden)
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(id);
            }
        }
    }

}
