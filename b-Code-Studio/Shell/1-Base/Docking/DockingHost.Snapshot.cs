using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;

namespace HistoryAurora.Shell.Base.Docking;

internal sealed partial class DockingHost
{
    private DockLayoutSnapshot CaptureLayoutSnapshot(LayoutRoot root)
    {
        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
        }
        if (!LayoutHasMainDocumentPane())
            throw new InvalidOperationException("布局中必须且只能存在一个中央主文档区");

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

    private void ApplyLayoutSnapshot(string payload)
    {
        var snapshot = DockLayoutSnapshotCodec.Deserialize(payload);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();

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
        var pane = new LayoutDocumentPane { ShowHeader = snapshot.ShowHeader };
        foreach (var content in snapshot.Contents)
        {
            if (RestoreContent(content, documentPane: true, seen) is { } restored)
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
            if (RestoreContent(content, documentPane: false, seen) is LayoutAnchorable restored)
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

    private LayoutContent? RestoreContent(
        DockContentSnapshot snapshot,
        bool documentPane,
        HashSet<string> seen)
    {
        if (!_byId.TryGetValue(snapshot.Id, out var descriptor))
            return null;
        if (!seen.Add(snapshot.Id))
            throw new InvalidDataException($"布局快照包含重复窗口: {snapshot.Id}");

        LayoutContent content = documentPane && UsesDocumentIdentity(descriptor)
            ? CreateDocument(descriptor)
            : CreateAnchorable(descriptor);
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
                if (RestoreContent(content, documentPane: false, seen) is LayoutAnchorable anchorable)
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

    private static void ApplyPosition(
        ILayoutPanelElement element,
        DockLayoutNodeSnapshot snapshot)
    {
        var width = RestoreLength(snapshot.DockWidth);
        var height = RestoreLength(snapshot.DockHeight);
        var minWidth = FiniteOrZero(snapshot.DockMinWidth);
        var minHeight = FiniteOrZero(snapshot.DockMinHeight);
        switch (element)
        {
            case LayoutPanel item:
                Set(item, width, height, minWidth, minHeight);
                break;
            case LayoutDocumentPane item:
                Set(item, width, height, minWidth, minHeight);
                break;
            case LayoutAnchorablePane item:
                Set(item, width, height, minWidth, minHeight);
                break;
            case LayoutDocumentPaneGroup item:
                Set(item, width, height, minWidth, minHeight);
                break;
            case LayoutAnchorablePaneGroup item:
                Set(item, width, height, minWidth, minHeight);
                break;
        }
    }

    private static bool TryGetPosition(
        ILayoutElement element,
        out GridLength width,
        out GridLength height,
        out double minWidth,
        out double minHeight)
    {
        switch (element)
        {
            case LayoutPanel item:
                (width, height, minWidth, minHeight) =
                    (item.DockWidth, item.DockHeight, item.DockMinWidth, item.DockMinHeight);
                return true;
            case LayoutDocumentPane item:
                (width, height, minWidth, minHeight) =
                    (item.DockWidth, item.DockHeight, item.DockMinWidth, item.DockMinHeight);
                return true;
            case LayoutAnchorablePane item:
                (width, height, minWidth, minHeight) =
                    (item.DockWidth, item.DockHeight, item.DockMinWidth, item.DockMinHeight);
                return true;
            case LayoutDocumentPaneGroup item:
                (width, height, minWidth, minHeight) =
                    (item.DockWidth, item.DockHeight, item.DockMinWidth, item.DockMinHeight);
                return true;
            case LayoutAnchorablePaneGroup item:
                (width, height, minWidth, minHeight) =
                    (item.DockWidth, item.DockHeight, item.DockMinWidth, item.DockMinHeight);
                return true;
            default:
                width = GridLength.Auto;
                height = GridLength.Auto;
                minWidth = 0;
                minHeight = 0;
                return false;
        }
    }

    private static void Set(
        LayoutPanel item,
        GridLength width,
        GridLength height,
        double minWidth,
        double minHeight)
    {
        item.DockWidth = width;
        item.DockHeight = height;
        item.DockMinWidth = minWidth;
        item.DockMinHeight = minHeight;
    }

    private static void Set(
        LayoutDocumentPane item,
        GridLength width,
        GridLength height,
        double minWidth,
        double minHeight)
    {
        item.DockWidth = width;
        item.DockHeight = height;
        item.DockMinWidth = minWidth;
        item.DockMinHeight = minHeight;
    }

    private static void Set(
        LayoutAnchorablePane item,
        GridLength width,
        GridLength height,
        double minWidth,
        double minHeight)
    {
        item.DockWidth = width;
        item.DockHeight = height;
        item.DockMinWidth = minWidth;
        item.DockMinHeight = minHeight;
    }

    private static void Set(
        LayoutDocumentPaneGroup item,
        GridLength width,
        GridLength height,
        double minWidth,
        double minHeight)
    {
        item.DockWidth = width;
        item.DockHeight = height;
        item.DockMinWidth = minWidth;
        item.DockMinHeight = minHeight;
    }

    private static void Set(
        LayoutAnchorablePaneGroup item,
        GridLength width,
        GridLength height,
        double minWidth,
        double minHeight)
    {
        item.DockWidth = width;
        item.DockHeight = height;
        item.DockMinWidth = minWidth;
        item.DockMinHeight = minHeight;
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
}
