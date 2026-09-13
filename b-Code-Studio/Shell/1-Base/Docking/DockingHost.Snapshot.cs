using System.IO;
using System.Windows.Controls;
using System.Windows;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 存取这一面：布局快照的序列化与恢复，以及建在它之上的场景切换（场景只是一份命名布局，REQ-UI-094）。
/// </summary>
internal sealed partial class DockingHost : ISceneDocking
{
    private DockLayoutSnapshot CaptureLayoutSnapshot(LayoutRoot root)
    {
        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
        }
        if (!LayoutHasMainDocumentPane())
            throw new InvalidOperationException("布局中找不到中央主文档区");

        return new DockLayoutSnapshot
        {
            Root = CaptureNode(root.RootPanel),
            FloatingWindows = root.FloatingWindows
                .Select(CaptureFloatingWindow)
                .Where(item => item != null)
                .Cast<DockFloatingWindowSnapshot>()
                .ToList(),
            AutoHideGroups = CaptureAutoHideGroups(root),
            Placements = CapturePlacements(),
            ActiveContentId = root.ActiveContent?.ContentId,
        };
    }

    private DockLayoutNodeSnapshot CaptureNode(ILayoutElement element)
    {
        var snapshot = element switch
        {
            LayoutPanel panel => new DockLayoutNodeSnapshot
            {
                Kind = DockLayoutNodeKind.Panel,
                Orientation = panel.Orientation.ToString(),
                Children = panel.Children.Select(CaptureNode).ToList(),
            },
            LayoutDocumentPane pane => new DockLayoutNodeSnapshot
            {
                Kind = DockLayoutNodeKind.DocumentPane,
                ShowHeader = pane.ShowHeader,
                SelectedContentId = pane.SelectedContent?.ContentId,
                Contents = pane.Children.Select(CaptureContent).Where(item => item != null)
                    .Cast<DockContentSnapshot>().ToList(),
            },
            LayoutAnchorablePane pane => new DockLayoutNodeSnapshot
            {
                Kind = DockLayoutNodeKind.AnchorablePane,
                SelectedContentId = pane.SelectedContent?.ContentId,
                Contents = pane.Children.Select(CaptureContent).Where(item => item != null)
                    .Cast<DockContentSnapshot>().ToList(),
            },
            LayoutDocumentPaneGroup group => new DockLayoutNodeSnapshot
            {
                Kind = DockLayoutNodeKind.DocumentPaneGroup,
                Orientation = group.Orientation.ToString(),
                Children = group.Children.Cast<ILayoutElement>().Select(CaptureNode).ToList(),
            },
            LayoutAnchorablePaneGroup group => new DockLayoutNodeSnapshot
            {
                Kind = DockLayoutNodeKind.AnchorablePaneGroup,
                Orientation = group.Orientation.ToString(),
                Children = group.Children.Cast<ILayoutElement>().Select(CaptureNode).ToList(),
            },
            _ => throw new InvalidDataException($"不支持的布局节点: {element.GetType().Name}"),
        };

        if (TryGetPosition(element, out var width, out var height, out var minWidth, out var minHeight))
        {
            snapshot = new DockLayoutNodeSnapshot
            {
                Kind = snapshot.Kind,
                Orientation = snapshot.Orientation,
                DockWidth = CaptureLength(width),
                DockHeight = CaptureLength(height),
                DockMinWidth = FiniteOrZero(minWidth),
                DockMinHeight = FiniteOrZero(minHeight),
                ShowHeader = snapshot.ShowHeader,
                SelectedContentId = snapshot.SelectedContentId,
                Children = snapshot.Children,
                Contents = snapshot.Contents,
            };
        }

        return snapshot;
    }

    private static DockContentSnapshot? CaptureContent(LayoutContent content)
        => string.IsNullOrWhiteSpace(content.ContentId)
            ? null
            : new DockContentSnapshot
            {
                Id = content.ContentId,
                FloatingLeft = FiniteOrZero(content.FloatingLeft),
                FloatingTop = FiniteOrZero(content.FloatingTop),
                FloatingWidth = FiniteOrZero(content.FloatingWidth),
                FloatingHeight = FiniteOrZero(content.FloatingHeight),
                IsMaximized = content.IsMaximized,
            };

    private DockFloatingWindowSnapshot? CaptureFloatingWindow(LayoutFloatingWindow window)
        => window switch
        {
            LayoutAnchorableFloatingWindow anchorable => new DockFloatingWindowSnapshot
            {
                Kind = "anchorable",
                Root = CaptureNode(anchorable.RootPanel),
            },
            LayoutDocumentFloatingWindow document => new DockFloatingWindowSnapshot
            {
                Kind = "document",
                Root = CaptureNode(document.RootPanel),
            },
            _ => null,
        };

    private static List<DockAutoHideGroupSnapshot> CaptureAutoHideGroups(LayoutRoot root)
    {
        var result = new List<DockAutoHideGroupSnapshot>();
        Add(root.LeftSide, DockSide.Left);
        Add(root.RightSide, DockSide.Right);
        Add(root.TopSide, DockSide.Top);
        Add(root.BottomSide, DockSide.Bottom);
        return result;

        void Add(LayoutAnchorSide side, DockSide dockSide)
        {
            foreach (var group in side.Children)
            {
                var contents = group.Children.Select(CaptureContent).Where(item => item != null)
                    .Cast<DockContentSnapshot>().ToList();
                if (contents.Count > 0)
                    result.Add(new DockAutoHideGroupSnapshot { Side = dockSide, Contents = contents });
            }
        }
    }

    /// <summary>恢复一份快照。返回这份快照认识的页（有位置记录或在树里）——之后才登记的页不在其中。</summary>
    private HashSet<string> ApplyLayoutSnapshot(string payload)
    {
        var snapshot = DockLayoutSnapshotCodec.Deserialize(payload);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _hiddenCenterIds.Clear();
        _orphanPlacements.Clear();
        _lastVisiblePlacements.Clear();

        foreach (var (id, placement) in snapshot.Placements)
        {
            _orphanPlacements[id] = placement;
            _lastVisiblePlacements[id] = placement;
        }

        var restoredRoot = RestoreNode(snapshot.Root ?? throw new InvalidDataException("布局快照缺少主布局树"), seen) as LayoutPanel
                           ?? throw new InvalidDataException("布局快照根节点必须是 Panel");
        var root = new LayoutRoot { RootPanel = restoredRoot };
        RestoreAutoHideGroups(root, snapshot.AutoHideGroups, seen);
        RestoreFloatingWindows(root, snapshot.FloatingWindows, seen);
        _manager.Layout = root;
        root.CollectGarbage();

        if (!string.IsNullOrWhiteSpace(snapshot.ActiveContentId))
        {
            var active = root.Descendents().OfType<LayoutContent>()
                .Concat(root.Hidden)
                .FirstOrDefault(item => string.Equals(
                    item.ContentId, snapshot.ActiveContentId, StringComparison.OrdinalIgnoreCase));
            if (active != null)
                root.ActiveContent = active;
        }

        seen.UnionWith(snapshot.Placements.Keys);
        return seen;
    }

    private ILayoutPanelElement? RestoreNode(
        DockLayoutNodeSnapshot snapshot,
        HashSet<string> seen)
    {
        ILayoutPanelElement result = snapshot.Kind switch
        {
            DockLayoutNodeKind.Panel => RestorePanel(snapshot, seen),
            DockLayoutNodeKind.DocumentPane => RestoreDocumentPane(snapshot, seen),
            DockLayoutNodeKind.AnchorablePane => RestoreAnchorablePane(snapshot, seen),
            DockLayoutNodeKind.DocumentPaneGroup => RestoreDocumentPaneGroup(snapshot, seen),
            DockLayoutNodeKind.AnchorablePaneGroup => RestoreAnchorablePaneGroup(snapshot, seen),
            _ => throw new InvalidDataException($"未知布局节点: {snapshot.Kind}"),
        };
        ApplyPosition(result, snapshot);
        return result;
    }

    private LayoutPanel RestorePanel(DockLayoutNodeSnapshot snapshot, HashSet<string> seen)
    {
        var panel = new LayoutPanel { Orientation = ParseOrientation(snapshot.Orientation) };
        foreach (var child in snapshot.Children)
        {
            if (RestoreNode(child, seen) is { } restored)
                panel.Children.Add(restored);
        }
        return panel;
    }

    private LayoutDocumentPane RestoreDocumentPane(
        DockLayoutNodeSnapshot snapshot,
        HashSet<string> seen)
    {
        var pane = new CenterDocumentPane { ShowHeader = snapshot.ShowHeader };
        foreach (var content in snapshot.Contents)
        {
            if (RestoreContent(content, seen) is { } restored)
                pane.Children.Add(restored);
        }
        Select(pane, snapshot.SelectedContentId);
        return pane;
    }

    private LayoutAnchorablePane RestoreAnchorablePane(
        DockLayoutNodeSnapshot snapshot,
        HashSet<string> seen)
    {
        var pane = new LayoutAnchorablePane();
        foreach (var content in snapshot.Contents)
        {
            if (RestoreContent(content, seen) is LayoutAnchorable restored)
                pane.Children.Add(restored);
        }
        Select(pane, snapshot.SelectedContentId);
        return pane;
    }

    private LayoutDocumentPaneGroup RestoreDocumentPaneGroup(
        DockLayoutNodeSnapshot snapshot,
        HashSet<string> seen)
    {
        var group = new LayoutDocumentPaneGroup { Orientation = ParseOrientation(snapshot.Orientation) };
        foreach (var child in snapshot.Children)
        {
            var restored = RestoreNode(child, seen);
            if (restored is ILayoutDocumentPane documentPane)
                group.Children.Add(documentPane);
        }
        return group;
    }

    private LayoutAnchorablePaneGroup RestoreAnchorablePaneGroup(
        DockLayoutNodeSnapshot snapshot,
        HashSet<string> seen)
    {
        var group = new LayoutAnchorablePaneGroup { Orientation = ParseOrientation(snapshot.Orientation) };
        foreach (var child in snapshot.Children)
        {
            var restored = RestoreNode(child, seen);
            if (restored is ILayoutAnchorablePane anchorablePane)
                group.Children.Add(anchorablePane);
        }
        return group;
    }

    /// <summary>
    /// 1.20.2 起所有页都是工具页（REQ-UI-102）：旧快照里命令集的文档节点在这里就地换成工具页。
    /// 因此**不再有** documentPane 这个参数——它在 1.20.2 就已经没人读了。
    /// </summary>
    private LayoutContent? RestoreContent(DockContentSnapshot snapshot, HashSet<string> seen)
    {
        if (!_byId.TryGetValue(snapshot.Id, out var descriptor))
            return null;
        if (!seen.Add(snapshot.Id))
            throw new InvalidDataException($"布局快照包含重复窗口: {snapshot.Id}");

        LayoutContent content = CreateAnchorable(descriptor);
        ApplyFloatingGeometry(content, snapshot);
        return content;
    }

    private void RestoreFloatingWindows(
        LayoutRoot root,
        IEnumerable<DockFloatingWindowSnapshot> windows,
        HashSet<string> seen)
    {
        foreach (var window in windows)
        {
            var restored = RestoreNode(window.Root, seen);
            switch (window.Kind)
            {
                case "anchorable" when restored is LayoutAnchorablePaneGroup anchorableGroup &&
                                         anchorableGroup.ChildrenCount > 0:
                    root.FloatingWindows.Add(new LayoutAnchorableFloatingWindow
                    {
                        RootPanel = anchorableGroup,
                    });
                    break;
                case "document" when restored is LayoutDocumentPaneGroup documentGroup &&
                                       documentGroup.ChildrenCount > 0:
                    root.FloatingWindows.Add(new LayoutDocumentFloatingWindow
                    {
                        RootPanel = documentGroup,
                    });
                    break;
            }
        }
    }

    private void RestoreFloatingWindowsAfterMaximize(LayoutRoot root, string payload)
    {
        var snapshot = DockLayoutSnapshotCodec.Deserialize(payload);
        var seen = root.RootPanel.Descendents()
            .OfType<LayoutContent>()
            .Select(item => item.ContentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        root.FloatingWindows.Clear();
        RestoreFloatingWindows(root, snapshot.FloatingWindows, seen);
    }

    private void RestoreAutoHideGroups(
        LayoutRoot root,
        IEnumerable<DockAutoHideGroupSnapshot> snapshots,
        HashSet<string> seen)
    {
        foreach (var snapshot in snapshots)
        {
            var group = new LayoutAnchorGroup();
            foreach (var content in snapshot.Contents)
            {
                if (RestoreContent(content, seen) is LayoutAnchorable anchorable)
                    group.Children.Add(anchorable);
            }
            if (group.ChildrenCount == 0)
                continue;

            var side = snapshot.Side switch
            {
                DockSide.Left => root.LeftSide,
                DockSide.Right => root.RightSide,
                DockSide.Top => root.TopSide,
                DockSide.Bottom => root.BottomSide,
                _ => null,
            };
            side?.Children.Add(group);
        }
    }

    /// <summary>
    /// 布局节点的尺寸四项（DockWidth / DockHeight / DockMinWidth / DockMinHeight）。
    ///
    /// 承载它们的是 AvalonDock 的 <c>ILayoutPositionableElement</c>，而那个接口是 internal——
    /// 拿不到接口，就只能按具体类型逐个 case。1.20.2 及以前这里是一段五路 switch 取值
    /// 加五个同名同体的 <c>Set</c> 重载，一百多行里没有一行是各自不同的判断。
    /// 而同一个 internal，本类的 <c>GetDockLength</c> / <c>SetDockLength</c> 早就用反射绕过去了：
    /// 同一个问题两种写法，改一处忘一处只是时间问题。1.20.3（REQ-UI-103）统一走反射。
    /// </summary>
    private static bool IsPositionable(ILayoutElement element)
        => element is LayoutPanel or LayoutDocumentPane or LayoutAnchorablePane
            or LayoutDocumentPaneGroup or LayoutAnchorablePaneGroup;

    private static void ApplyPosition(ILayoutPanelElement element, DockLayoutNodeSnapshot snapshot)
    {
        if (!IsPositionable(element))
            return;

        SetDockLength(element, horizontal: true, RestoreLength(snapshot.DockWidth));
        SetDockLength(element, horizontal: false, RestoreLength(snapshot.DockHeight));
        SetDouble(element, "DockMinWidth", FiniteOrZero(snapshot.DockMinWidth));
        SetDouble(element, "DockMinHeight", FiniteOrZero(snapshot.DockMinHeight));
    }

    private static bool TryGetPosition(
        ILayoutElement element,
        out GridLength width,
        out GridLength height,
        out double minWidth,
        out double minHeight)
    {
        width = GridLength.Auto;
        height = GridLength.Auto;
        minWidth = 0;
        minHeight = 0;
        if (!IsPositionable(element))
            return false;

        width = GetDockLength(element, horizontal: true);
        height = GetDockLength(element, horizontal: false);
        minWidth = GetDouble(element, "DockMinWidth");
        minHeight = GetDouble(element, "DockMinHeight");
        return true;
    }

    private static double GetDouble(ILayoutElement element, string name)
        => element.GetType().GetProperty(name)?.GetValue(element) is double value ? value : 0;

    private static void SetDouble(ILayoutElement element, string name, double value)
    {
        var property = element.GetType().GetProperty(name);
        if (property is { CanWrite: true })
            property.SetValue(element, value);
    }

    private static void ApplyFloatingGeometry(
        LayoutContent content,
        DockContentSnapshot snapshot)
    {
        var workArea = SystemParameters.WorkArea;
        var width = NormalizeFloatingSize(snapshot.FloatingWidth, 640, workArea.Width);
        var height = NormalizeFloatingSize(snapshot.FloatingHeight, 480, workArea.Height);
        var left = double.IsFinite(snapshot.FloatingLeft) ? snapshot.FloatingLeft : workArea.Left;
        var top = double.IsFinite(snapshot.FloatingTop) ? snapshot.FloatingTop : workArea.Top;

        var virtualArea = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var proposed = new Rect(left, top, width, height);
        if (!virtualArea.IntersectsWith(proposed))
        {
            left = workArea.Left + Math.Min(32, Math.Max(0, workArea.Width - width));
            top = workArea.Top + Math.Min(32, Math.Max(0, workArea.Height - height));
        }
        else
        {
            left = Math.Clamp(left, virtualArea.Left, Math.Max(virtualArea.Left, virtualArea.Right - width));
            top = Math.Clamp(top, virtualArea.Top, Math.Max(virtualArea.Top, virtualArea.Bottom - height));
        }

        content.FloatingLeft = left;
        content.FloatingTop = top;
        content.FloatingWidth = width;
        content.FloatingHeight = height;
        content.IsMaximized = snapshot.IsMaximized;
    }

    private static double NormalizeFloatingSize(double value, double fallback, double maximum)
    {
        var safeMaximum = double.IsFinite(maximum) && maximum > 0 ? maximum : fallback;
        var safe = double.IsFinite(value) && value > 0 ? value : fallback;
        return Math.Clamp(safe, Math.Min(160, safeMaximum), safeMaximum);
    }

    private static void Select(LayoutDocumentPane pane, string? id)
    {
        var index = pane.Children.ToList().FindIndex(item =>
            string.Equals(item.ContentId, id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            pane.SelectedContentIndex = index;
    }

    private static void Select(LayoutAnchorablePane pane, string? id)
    {
        var index = pane.Children.ToList().FindIndex(item =>
            string.Equals(item.ContentId, id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            pane.SelectedContentIndex = index;
    }

    private static Orientation ParseOrientation(string? value)
        => Enum.TryParse<Orientation>(value, ignoreCase: true, out var orientation)
            ? orientation
            : Orientation.Horizontal;

    private static DockLengthSnapshot CaptureLength(GridLength value)
        => new(FiniteOrZero(value.Value), value.GridUnitType switch
        {
            GridUnitType.Auto => DockLengthUnit.Auto,
            GridUnitType.Pixel => DockLengthUnit.Pixel,
            _ => DockLengthUnit.Star,
        });

    private static GridLength RestoreLength(DockLengthSnapshot snapshot)
    {
        var value = double.IsFinite(snapshot.Value) && snapshot.Value >= 0 ? snapshot.Value : 1;
        return snapshot.Unit switch
        {
            DockLengthUnit.Auto => GridLength.Auto,
            DockLengthUnit.Pixel => new GridLength(value, GridUnitType.Pixel),
            _ => new GridLength(value > 0 ? value : 1, GridUnitType.Star),
        };
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;


    // ---------------------------------------------------------------- 场景（REQ-UI-085 / 094）

    /// <summary>
    /// 切到一个场景：换掉停靠布局。
    ///
    /// 场景只是一份命名布局（REQ-UI-094），<paramref name="seed"/> 是它的**初值**——
    /// 场景没有布局可恢复时，哪些页露面。布局的来路按先后：
    /// <list type="number">
    ///   <item><paramref name="rebuild"/> 为 true：按各页 placement 重建默认布局，再按初值定显隐（场景重置）；</item>
    ///   <item>存有同名命名布局：恢复它，显隐就是它记着的样子。存下之后才登记的页这个场景没见过，按初值定；</item>
    ///   <item>都没有：保留当前布局树，按初值定显隐。</item>
    /// </list>
    /// 一格一页（REQ-UI-100）：按初值露面的页不顶掉任何一页，位置被占着就不露面；
    /// 只有 <paramref name="prefer"/> 里的页（模块场景传该模块自己的页）会顶掉它位置上的页——
    /// 于是 Janus 的「图」与常驻的控制台同一格时，进 Janus 露「图」。
    ///
    /// **页面视图不重建**：内容对象按 id 缓存在 <c>_contents</c>，恢复快照换的只是停靠模型
    /// （REQ-UI-086）。切走再切回来，页面里选好的来源、表格的选中行都还在。
    /// </summary>
    public void ApplyScene(
        string name,
        IReadOnlyCollection<string> seed,
        bool rebuild,
        IReadOnlyCollection<string>? prefer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(seed);
        RestoreLayoutFromMaximized();
        SetRegistrationScene(name);

        var initial = new HashSet<string>(seed, StringComparer.OrdinalIgnoreCase);
        var preferred = new HashSet<string>(prefer ?? [], StringComparer.OrdinalIgnoreCase);
        string? payload = null;
        var defaultsName = name + ".defaults";
        if (!rebuild || _store.ReadNamed(defaultsName) != null)
        {
            try
            {
                payload = _store.ReadNamed(rebuild ? defaultsName : name);
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"读取场景布局 {name} 失败，保留当前布局只按初值定显隐: {ex.Message}");
            }
        }

        using (Suppress())
        {
            var restored = false;
            if (payload != null)
            {
                try
                {
                    var known = ApplyLayoutSnapshot(payload);
                    if (!LayoutHasMainDocumentPane())
                        throw new InvalidOperationException("布局中缺少中央主文档区");
                    EnsureRegisteredWindows();
                    // 1.20.1 及以前存下的场景布局里一格可能有好几页：每格留选中的那一页。
                    EvictExtraPages();
                    SeedVisibility(name, _descriptors.Where(d => !known.Contains(d.Id)).ToArray(), initial, preferred);

                    restored = true;
                    rebuild = false;
                    _seedRatiosFromLayout = true;
                }
                catch (Exception ex)
                {
                    _log.Warn(LayoutSource, $"场景布局 {name} 无法恢复，按默认布局重建: {ex.Message}");
                    rebuild = true;
                }
            }

            if (rebuild)
            {
                _orphanPlacements.Clear();
                _lastVisiblePlacements.Clear();
                BuildDefaultLayout();
                _seedRatiosFromLayout = false;
                foreach (var d in _descriptors)
                    _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
            }

            if (!restored)
                SeedVisibility(name, _descriptors.ToArray(), initial, preferred);

            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = name;
        }

        ScheduleReapplyRatios();
        RebaseSoon();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按初值定显隐，分三步：不在初值里的藏起来；在初值里、默认就该露面而此刻藏着的，位置空着才露面；
    /// 最后 <paramref name="preferred"/> 里的页露面并顶掉它位置上的页。
    /// 默认就不显示的页（DefaultVisible = false）不替它做主。
    /// </summary>
    private void SeedVisibility(
        string scene,
        IReadOnlyList<ToolWindowDescriptor> descriptors,
        HashSet<string> initial,
        HashSet<string> preferred)
    {
        foreach (var descriptor in descriptors.Where(d => !initial.Contains(d.Id)))
            Seed(scene, descriptor.Id, () => HidePage(FindRequiredAnchorable(descriptor.Id)));

        var shown = descriptors
            .Where(d => initial.Contains(d.Id) && d.DefaultVisible)
            .OrderBy(d => preferred.Contains(d.Id))
            .ToArray();
        foreach (var descriptor in shown)
        {
            var id = descriptor.Id;
            if (preferred.Contains(id))
                Seed(scene, id, () => Show(id));
            else if (!ComputeState(id).Visible)
                Seed(scene, id, () => ShowIfSeatFree(id));
        }
    }

    private void Seed(string scene, string id, Action action)
    {
        try
        {
            EnsureRegistered(id);
            action();
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"场景 {scene} 处理窗口 {id} 失败: {ex.Message}");
        }
    }
}
