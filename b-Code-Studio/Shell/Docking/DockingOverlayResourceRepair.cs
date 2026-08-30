using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private const double PreviewOpacity = 0.55;

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
            if (control.Template?.FindName("PART_PreviewBox", control) is not Path preview ||
                preview.Data is not Geometry geometry)
                return false;

            var bounds = geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return false;

            // RenderTransformOrigin is expressed in the Path element's
            // arranged bounds. Using Geometry.Bounds as the transform center
            // is unreliable when AvalonDock stretches the template path, and
            // leaves the full-size blue rectangle visible.
            preview.RenderTransformOrigin = new Point(0.5, 0.5);
            preview.RenderTransform = new ScaleTransform(PreviewScale, PreviewScale);
            // A target can legitimately be the whole document area. The
            // overlay must still let neighbouring floating windows remain
            // readable while the border and direction buttons show the target.
            preview.Opacity = PreviewOpacity;
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
            new SolidColorBrush(dark ? Color.FromArgb(0x38, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
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
            new SolidColorBrush(dark ? Color.FromArgb(0x38, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey), changed);
        PutAlways(overlay.Resources, ResourceKeys.PreviewBoxBackgroundBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0x38, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
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
            new SolidColorBrush(dark ? Color.FromArgb(0x38, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
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
