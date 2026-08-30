using System.Windows;
using System.Windows.Media;
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
    internal static void Ensure(FrameworkElement overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        var dark = (overlay.TryFindResource("Aurora.Brush.Surface") as SolidColorBrush)?.Color is { } surface &&
                   (surface.R + surface.G + surface.B) < 288;

        PutIfTransparent(overlay, ResourceKeys.DockingButtonBackgroundBrushKey,
            Brushes.Transparent);
        PutIfTransparent(overlay, ResourceKeys.DockingButtonForegroundBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)));
        PutIfTransparent(overlay, ResourceKeys.DockingButtonForegroundArrowBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x93, 0xC5, 0xFD) : Color.FromRgb(0x1D, 0x4E, 0xD8)));
        PutIfTransparent(overlay, ResourceKeys.DockingButtonStarBorderBrushKey,
            Brushes.Transparent);
        PutIfTransparent(overlay, ResourceKeys.DockingButtonStarBackgroundBrushKey,
            Brushes.Transparent);
        PutIfTransparent(overlay, ResourceKeys.PreviewBoxBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)));
        PutIfTransparent(overlay, ResourceKeys.PreviewBoxBackgroundBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0x70, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x60, 0x3B, 0x82, 0xF6)));
    }

    private static void PutIfTransparent(FrameworkElement overlay, object key, Brush fallback)
    {
        var actualKey = FindEquivalentComponentKey(overlay.Resources, key) ?? key;
        if (overlay.TryFindResource(actualKey) is Brush brush && brush.Opacity > 0 &&
            (brush is not SolidColorBrush solid || solid.Color.A > 0))
        {
            return;
        }

        overlay.Resources[actualKey] = fallback;
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
