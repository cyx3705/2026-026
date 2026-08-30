using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AvalonDock.Controls;
using AvalonDock.Themes.VS2013.Themes;

namespace HistoryAurora.Shell.Docking;

/// <summary>
/// Supplies the docking overlay's component-resource brushes in the overlay
/// window itself. AvalonDock normally inherits these from its theme dictionary,
/// but a module loaded into a collectible context can leave DynamicResource
/// references bound to a transparent palette fallback.
/// </summary>
internal static class DockingOverlayResourceRepair
{
    // The preview path is drawn in a full-screen overlay window. Keep the
    // visual hint compact; AvalonDock's hit-test rectangle remains unchanged.
    private const double PreviewScale = 0.35;
    private const double DockingIndicatorSize = 28;
    private const double AnchorableStarSize = 122;
    private const double DocumentStarSize = 122;
    private const double FullDocumentStarSize = 204;

    private static readonly string[] IndicatorGridNames =
    [
        "PART_DockingManagerDropTargets",
        "PART_AnchorablePaneDropTargets",
        "PART_DocumentPaneDropTargets",
        "PART_DocumentPaneFullDropTargets",
    ];

    /// <summary>
    /// 只缩放覆盖层中的预览几何，不改变 AvalonDock 用来命中投放的矩形。
    /// PART_PreviewBox 是一个占满 OverlayWindow 的 Path，几何本身才是目标页
    /// 的边界；缩放几何可避免它把相邻浮窗视觉上整块盖住，同时保留完整停靠命中。
    /// </summary>
    internal static bool EnsurePreviewScale(FrameworkElement overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay is not Control control)
            return false;

        try
        {
            control.ApplyTemplate();
            // OverlayWindow assigns Path.Data later, from
            // IOverlayWindow.DragEnter(IDropTarget). The visual guard must be
            // installed before that assignment; otherwise the first sample
            // returns early and the full-size path remains untouched.
            if (control.Template?.FindName("PART_PreviewBox", control) is not Path preview)
                return false;

            // RenderTransformOrigin is expressed in the Path element's
            // arranged bounds. Using Geometry.Bounds as the transform center
            // is unreliable when AvalonDock stretches the template path, and
            // leaves the full-size blue rectangle visible.
            preview.RenderTransformOrigin = new Point(0.5, 0.5);
            preview.RenderTransform = new ScaleTransform(PreviewScale, PreviewScale);
            // A target can legitimately be the whole document area. A filled
            // preview path therefore occludes every window underneath it;
            // keep the target outline while making the fill unconditionally
            // transparent. The direction buttons remain the active visual
            // affordance and AvalonDock still uses its original hit geometry.
            preview.Fill = Brushes.Transparent;
            preview.Opacity = 1.0;
            return true;
        }
        catch (InvalidOperationException)
        {
            // AvalonDock may be rebuilding the overlay template during a move.
            // The next probe sample retries; no docking state is touched here.
            return false;
        }
    }

    /// <summary>
    /// Prevents AvalonDock's indicator content from inheriting the full screen
    /// detection rectangle. The four indicator grids deliberately keep their
    /// arranged size because AvalonDock uses that size for hit testing; only
    /// their visual children are constrained here.
    /// </summary>
    internal static bool EnsureDockingIndicatorVisuals(FrameworkElement overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (overlay is not Control control)
            return false;

        try
        {
            control.ApplyTemplate();
            if (control.Template is null)
                return false;

            var changed = false;
            foreach (var gridName in IndicatorGridNames)
            {
                if (control.Template.FindName(gridName, control) is not FrameworkElement grid)
                    continue;

                var starSize = gridName == "PART_DocumentPaneFullDropTargets"
                    ? FullDocumentStarSize
                    : gridName == "PART_DockingManagerDropTargets"
                        ? 0
                        : gridName == "PART_DocumentPaneDropTargets"
                            ? DocumentStarSize
                            : AnchorableStarSize;

                if (starSize > 0)
                    changed |= ConstrainDirectStarPath(grid, starSize);

                changed |= ConstrainIndicatorChildren(grid);
            }

            return changed;
        }
        catch (InvalidOperationException)
        {
            // AvalonDock can replace the template while a drag is moving.
            // The next probe sample retries without touching drag state.
            return false;
        }
    }

    /// <summary>
    /// Emits the actual layout chain for the indicator controls. This is kept
    /// separate from the repair so a future theme change can be diagnosed from
    /// logs without changing the docking behavior.
    /// </summary>
    internal static string DescribeDockingIndicatorVisuals(FrameworkElement overlay)
    {
        if (overlay is not Control control || control.Template is null)
            return "模板=无";

        var parts = new List<string>();
        foreach (var gridName in IndicatorGridNames)
        {
            if (control.Template.FindName(gridName, control) is not FrameworkElement grid)
            {
                parts.Add($"{gridName}=缺失");
                continue;
            }

            var details = new List<string>
            {
                $"{gridName}[{DescribeSize(grid)}]"
            };
            if (gridName != "PART_DockingManagerDropTargets")
            {
                foreach (var child in DirectChildren(grid))
                {
                    if (child is Path path)
                        details.Add($"Path[{DescribeSize(path)}]");
                }
            }

            foreach (var element in WalkElements(grid))
            {
                if (element is ContentControl content &&
                    element.Name.Contains("DropTarget", StringComparison.OrdinalIgnoreCase))
                {
                    var contentSize = content.Content is FrameworkElement contentElement
                        ? $" content={contentElement.GetType().Name}[{DescribeSize(contentElement)}]"
                        : " content=null";
                    details.Add($"{element.Name}[{DescribeSize(element)}]{contentSize}");
                }
            }

            parts.Add(string.Join(",", details));
        }

        return parts.Count == 0 ? "无部件" : string.Join(";", parts);
    }

    private static bool ConstrainDirectStarPath(FrameworkElement grid, double size)
    {
        var changed = false;
        foreach (var child in DirectChildren(grid))
        {
            if (child is not Path path)
                continue;

            changed |= SetDimension(path, FrameworkElement.WidthProperty, size);
            changed |= SetDimension(path, FrameworkElement.HeightProperty, size);
            changed |= SetAlignment(path);
            if (path.Stretch != Stretch.Uniform)
            {
                path.Stretch = Stretch.Uniform;
                changed = true;
            }
        }

        return changed;
    }

    private static bool ConstrainIndicatorChildren(FrameworkElement grid)
    {
        var changed = false;
        foreach (var element in WalkElements(grid))
        {
            if (element is not ContentControl content ||
                !element.Name.Contains("DropTarget", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A missing DynamicResource resolves as NaN and lets the
            // ContentControl stretch to the detection rectangle. A stale
            // hot-reload template can likewise retain a huge value. The
            // control is a small hit target by design, so repair both cases.
            if (double.IsNaN(content.Width) || content.Width > DockingIndicatorSize * 2)
                changed |= SetDimension(content, FrameworkElement.WidthProperty, DockingIndicatorSize);
            if (double.IsNaN(content.Height) || content.Height > DockingIndicatorSize * 2)
                changed |= SetDimension(content, FrameworkElement.HeightProperty, DockingIndicatorSize);
            if (content.HorizontalContentAlignment != HorizontalAlignment.Center)
            {
                content.HorizontalContentAlignment = HorizontalAlignment.Center;
                changed = true;
            }

            if (content.VerticalContentAlignment != VerticalAlignment.Center)
            {
                content.VerticalContentAlignment = VerticalAlignment.Center;
                changed = true;
            }

            if (content.Content is FrameworkElement visual)
            {
                changed |= SetDimension(visual, FrameworkElement.WidthProperty, DockingIndicatorSize);
                changed |= SetDimension(visual, FrameworkElement.HeightProperty, DockingIndicatorSize);
                changed |= SetAlignment(visual);
                if (visual is Viewbox viewbox && viewbox.Stretch != Stretch.Uniform)
                {
                    viewbox.Stretch = Stretch.Uniform;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool SetDimension(FrameworkElement element, DependencyProperty property, double value)
    {
        if (Equals(element.GetValue(property), value))
            return false;

        element.SetValue(property, value);
        return true;
    }

    private static bool SetAlignment(FrameworkElement element)
    {
        var changed = false;
        if (element.HorizontalAlignment != HorizontalAlignment.Center)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            changed = true;
        }

        if (element.VerticalAlignment != VerticalAlignment.Center)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            changed = true;
        }

        return changed;
    }

    private static IEnumerable<FrameworkElement> WalkElements(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is FrameworkElement element)
                yield return element;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static IEnumerable<DependencyObject> DirectChildren(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            yield return VisualTreeHelper.GetChild(root, i);
    }

    private static string DescribeSize(FrameworkElement element)
    {
        var render = element.RenderTransform?.GetType().Name ?? "无";
        var layout = element.LayoutTransform?.GetType().Name ?? "无";
        return $"actual={Math.Round(element.ActualWidth)}x{Math.Round(element.ActualHeight)} " +
               $"layout={FormatDimension(element.Width)}x{FormatDimension(element.Height)} " +
               $"render={render} layoutTransform={layout}";
    }

    private static string FormatDimension(double value)
        => double.IsNaN(value) ? "NaN" : Math.Round(value).ToString();

    /// <summary>
    /// AvalonDock owns one reusable overlay per overlay host. A drag can
    /// therefore have more than one OverlayWindow in Application.Windows
    /// (the main manager plus floating hosts). Guard every live instance so a
    /// stale host cannot leave an unscaled full-screen preview above the new
    /// one. This is intentionally called from the drag probe, not a process-
    /// lifetime event handler, so module hot reload does not retain this
    /// assembly through WPF's global class-handler table.
    /// </summary>
    internal static int EnsureOpenOverlayVisuals()
    {
        var windows = Application.Current?.Windows;
        if (windows is null)
            return 0;

        var guarded = 0;
        foreach (Window window in windows)
        {
            if (window is not OverlayWindow overlay)
                continue;

            var previewGuarded = EnsurePreviewScale(overlay);
            var indicatorGuarded = EnsureDockingIndicatorVisuals(overlay);
            if (previewGuarded || indicatorGuarded)
                guarded++;
        }

        return guarded;
    }

    /// <summary>
    /// 固化当前 Aurora 实例的停靠画刷字典。
    ///
    /// AvalonDock 会在拖动开始时从 ThemeResourceDictionary 建独立的覆盖窗。
    /// 这里必须在 DockingManager.Theme 赋值前完成；拖动途中再改字典会让
    /// AvalonDock 正在遍历的投放树失效，而且热重载后的第一帧已经太迟。
    /// </summary>
    internal static IReadOnlyList<string> EnsureThemeDictionary(ResourceDictionary theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var changed = new List<string>();
        var dark = (FindResource(theme, "Aurora.Brush.Surface") as SolidColorBrush)?.Color is { } surface &&
                   (surface.R + surface.G + surface.B) < 288;
        foreach (var (name, key, fallback) in Fallbacks(dark))
            PutIfTransparent(theme, key, fallback, name, changed);

        PutAlways(theme, ResourceKeys.PreviewBoxBackgroundBrushKey,
            Brushes.Transparent,
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey), changed);

        foreach (var merged in theme.MergedDictionaries)
            EnsureThemeDictionary(merged, changed, dark);

        return changed;
    }

    internal static DockingOverlayResourceRepairResult Ensure(FrameworkElement overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        var changed = new List<string>();

        var dark = (overlay.TryFindResource("Aurora.Brush.Surface") as SolidColorBrush)?.Color is { } surface &&
                   (surface.R + surface.G + surface.B) < 288;

        PutIfTransparent(overlay, ResourceKeys.DockingButtonBackgroundBrushKey,
            Brushes.Transparent, nameof(ResourceKeys.DockingButtonBackgroundBrushKey), changed);
        PutIfTransparent(overlay, ResourceKeys.DockingButtonForegroundBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)),
            nameof(ResourceKeys.DockingButtonForegroundBrushKey), changed);
        PutIfTransparent(overlay, ResourceKeys.DockingButtonForegroundArrowBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x93, 0xC5, 0xFD) : Color.FromRgb(0x1D, 0x4E, 0xD8)),
            nameof(ResourceKeys.DockingButtonForegroundArrowBrushKey), changed);
        PutIfTransparent(overlay, ResourceKeys.DockingButtonStarBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0xA0, 0x60, 0xA5, 0xFA) : Color.FromArgb(0x80, 0x60, 0xA5, 0xFA)),
            nameof(ResourceKeys.DockingButtonStarBorderBrushKey), changed);
        PutIfTransparent(overlay, ResourceKeys.DockingButtonStarBackgroundBrushKey,
            Brushes.Transparent, nameof(ResourceKeys.DockingButtonStarBackgroundBrushKey), changed);
        PutIfTransparent(overlay, ResourceKeys.PreviewBoxBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)),
            nameof(ResourceKeys.PreviewBoxBorderBrushKey), changed);
        PutIfTransparent(overlay, ResourceKeys.PreviewBoxBackgroundBrushKey,
            Brushes.Transparent,
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey), changed);
        PutAlways(overlay.Resources, ResourceKeys.PreviewBoxBackgroundBrushKey,
            Brushes.Transparent,
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey), changed);

        return new DockingOverlayResourceRepairResult(dark, changed);
    }

    private static void EnsureThemeDictionary(
        ResourceDictionary dictionary,
        ICollection<string> changed,
        bool dark)
    {
        foreach (var (name, key, fallback) in Fallbacks(dark))
            PutIfTransparent(dictionary, key, fallback, name, changed);

        PutAlways(dictionary, ResourceKeys.PreviewBoxBackgroundBrushKey,
            Brushes.Transparent,
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey), changed);

        foreach (var merged in dictionary.MergedDictionaries)
            EnsureThemeDictionary(merged, changed, dark);
    }

    private static IEnumerable<(string Name, object Key, Brush Fallback)> Fallbacks(bool dark)
    {
        yield return (nameof(ResourceKeys.DockingButtonBackgroundBrushKey),
            ResourceKeys.DockingButtonBackgroundBrushKey, Brushes.Transparent);
        yield return (nameof(ResourceKeys.DockingButtonForegroundBrushKey),
            ResourceKeys.DockingButtonForegroundBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)));
        yield return (nameof(ResourceKeys.DockingButtonForegroundArrowBrushKey),
            ResourceKeys.DockingButtonForegroundArrowBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x93, 0xC5, 0xFD) : Color.FromRgb(0x1D, 0x4E, 0xD8)));
        yield return (nameof(ResourceKeys.DockingButtonStarBorderBrushKey),
            ResourceKeys.DockingButtonStarBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0xA0, 0x60, 0xA5, 0xFA) : Color.FromArgb(0x80, 0x60, 0xA5, 0xFA)));
        yield return (nameof(ResourceKeys.DockingButtonStarBackgroundBrushKey),
            ResourceKeys.DockingButtonStarBackgroundBrushKey, Brushes.Transparent);
        yield return (nameof(ResourceKeys.PreviewBoxBorderBrushKey),
            ResourceKeys.PreviewBoxBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)));
        yield return (nameof(ResourceKeys.PreviewBoxBackgroundBrushKey),
            ResourceKeys.PreviewBoxBackgroundBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0x70, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x60, 0x3B, 0x82, 0xF6)));
    }

    private static void PutIfTransparent(
        FrameworkElement overlay,
        object key,
        Brush fallback,
        string name,
        ICollection<string> changed)
    {
        var actualKey = FindEquivalentComponentKey(overlay.Resources, key) ?? key;
        if (overlay.TryFindResource(actualKey) is Brush brush && brush.Opacity > 0 &&
            (brush is not SolidColorBrush solid || solid.Color.A > 0))
        {
            return;
        }

        overlay.Resources[actualKey] = fallback;
        changed.Add(name);
    }

    private static void PutAlways(
        ResourceDictionary dictionary,
        object key,
        Brush value,
        string name,
        ICollection<string> changed)
    {
        var actualKey = FindEquivalentComponentKey(dictionary, key) ?? key;
        dictionary[actualKey] = value;
        if (!changed.Contains(name))
            changed.Add(name);
    }

    private static void PutIfTransparent(
        ResourceDictionary dictionary,
        object key,
        Brush fallback,
        string name,
        ICollection<string> changed)
    {
        var actualKey = FindEquivalentComponentKey(dictionary, key) ?? key;
        var value = FindResource(dictionary, actualKey);
        if (value is Brush brush && brush.Opacity > 0 &&
            (brush is not SolidColorBrush solid || solid.Color.A > 0))
        {
            return;
        }

        dictionary[actualKey] = fallback;
        changed.Add(name);
    }

    private static object? FindResource(ResourceDictionary dictionary, object key)
    {
        if (dictionary.Contains(key))
            return dictionary[key];

        foreach (var merged in dictionary.MergedDictionaries)
        {
            var found = FindResource(merged, key);
            if (found != null)
                return found;
        }

        return null;
    }

    private static object? FindEquivalentComponentKey(ResourceDictionary dictionary, object requested)
    {
        if (requested is not ComponentResourceKey desired)
            return null;

        foreach (var candidate in dictionary.Keys)
        {
            if (candidate is ComponentResourceKey component &&
                Equals(component.ResourceId, desired.ResourceId))
            {
                return candidate;
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            var found = FindEquivalentComponentKey(merged, requested);
            if (found != null)
                return found;
        }

        return null;
    }
}

internal sealed record DockingOverlayResourceRepairResult(bool DarkTheme, IReadOnlyList<string> ChangedKeys);
