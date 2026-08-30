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
            new SolidColorBrush(dark ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x20, 0, 0, 0)));
        PutIfTransparent(overlay, ResourceKeys.DockingButtonForegroundBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)));
        PutIfTransparent(overlay, ResourceKeys.DockingButtonForegroundArrowBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x93, 0xC5, 0xFD) : Color.FromRgb(0x1D, 0x4E, 0xD8)));
        PutIfTransparent(overlay, ResourceKeys.DockingButtonStarBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0xA0, 0x60, 0xA5, 0xFA) : Color.FromArgb(0x80, 0x60, 0xA5, 0xFA)));
        PutIfTransparent(overlay, ResourceKeys.DockingButtonStarBackgroundBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0x30, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x20, 0x3B, 0x82, 0xF6)));
        PutIfTransparent(overlay, ResourceKeys.PreviewBoxBorderBrushKey,
            new SolidColorBrush(dark ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB)));
        PutIfTransparent(overlay, ResourceKeys.PreviewBoxBackgroundBrushKey,
            new SolidColorBrush(dark ? Color.FromArgb(0x70, 0x3B, 0x82, 0xF6) : Color.FromArgb(0x60, 0x3B, 0x82, 0xF6)));
    }

    private static void PutIfTransparent(FrameworkElement overlay, object key, Brush fallback)
    {
        if (overlay.TryFindResource(key) is Brush brush && brush.Opacity > 0 &&
            (brush is not SolidColorBrush solid || solid.Color.A > 0))
        {
            return;
        }

        overlay.Resources[key] = fallback;
    }
}
