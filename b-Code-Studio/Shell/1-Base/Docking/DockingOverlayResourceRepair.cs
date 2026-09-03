using System.Windows;
using System.Windows.Media;
using AvalonDock.Themes.VS2013.Themes;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// Prepares the resource dictionary used by AvalonDock's overlay windows.
///
/// This class deliberately has no visual-tree mutation API. OverlayWindow's
/// template owns the separation between hit targets and indicator artwork;
/// changing Width, Height, Template or Transform during WM_MOVING changes the
/// rectangles consumed by AvalonDock's DragService and is therefore forbidden.
/// </summary>
internal static class DockingOverlayResourceRepair
{
    /// <summary>
    /// Installs fallback brushes before DockingManager.Theme is assigned. The
    /// overlay reuses this dictionary instance, so no repair is needed during a
    /// drag and no collectible-context URI lookup is performed by the probe.
    /// </summary>
    internal static IReadOnlyList<string> EnsureThemeDictionary(ResourceDictionary theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var changed = new List<string>();
        var dark = (FindResource(theme, "Aurora.Brush.Surface") as SolidColorBrush)?.Color is { } surface &&
                   (surface.R + surface.G + surface.B) < 288;

        EnsureThemeDictionary(theme, changed, dark);
        return changed;
    }

    private static void EnsureThemeDictionary(
        ResourceDictionary dictionary,
        ICollection<string> changed,
        bool dark)
    {
        foreach (var (name, key, fallback) in Fallbacks(dark))
            PutIfTransparent(dictionary, key, fallback, name, changed);

        // The preview rectangle is a hit-test-independent outline. Its fill
        // must remain transparent so a full document target cannot cover other
        // floating windows.
        PutAlways(
            dictionary,
            ResourceKeys.PreviewBoxBackgroundBrushKey,
            Brushes.Transparent,
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey),
            changed);

        foreach (var merged in dictionary.MergedDictionaries)
            EnsureThemeDictionary(merged, changed, dark);
    }

    private static IEnumerable<(string Name, object Key, Brush Fallback)> Fallbacks(bool dark)
    {
        yield return (
            nameof(ResourceKeys.DockingButtonBackgroundBrushKey),
            ResourceKeys.DockingButtonBackgroundBrushKey,
            Brushes.Transparent);
        yield return (
            nameof(ResourceKeys.DockingButtonForegroundBrushKey),
            ResourceKeys.DockingButtonForegroundBrushKey,
            new SolidColorBrush(dark
                ? Color.FromRgb(0x60, 0xA5, 0xFA)
                : Color.FromRgb(0x25, 0x63, 0xEB)));
        yield return (
            nameof(ResourceKeys.DockingButtonForegroundArrowBrushKey),
            ResourceKeys.DockingButtonForegroundArrowBrushKey,
            new SolidColorBrush(dark
                ? Color.FromRgb(0x93, 0xC5, 0xFD)
                : Color.FromRgb(0x1D, 0x4E, 0xD8)));
        yield return (
            nameof(ResourceKeys.DockingButtonStarBorderBrushKey),
            ResourceKeys.DockingButtonStarBorderBrushKey,
            new SolidColorBrush(dark
                ? Color.FromArgb(0xA0, 0x60, 0xA5, 0xFA)
                : Color.FromArgb(0x80, 0x60, 0xA5, 0xFA)));
        yield return (
            nameof(ResourceKeys.DockingButtonStarBackgroundBrushKey),
            ResourceKeys.DockingButtonStarBackgroundBrushKey,
            Brushes.Transparent);
        yield return (
            nameof(ResourceKeys.PreviewBoxBorderBrushKey),
            ResourceKeys.PreviewBoxBorderBrushKey,
            new SolidColorBrush(dark
                ? Color.FromRgb(0x60, 0xA5, 0xFA)
                : Color.FromRgb(0x25, 0x63, 0xEB)));
        yield return (
            nameof(ResourceKeys.PreviewBoxBackgroundBrushKey),
            ResourceKeys.PreviewBoxBackgroundBrushKey,
            new SolidColorBrush(dark
                ? Color.FromArgb(0x70, 0x3B, 0x82, 0xF6)
                : Color.FromArgb(0x60, 0x3B, 0x82, 0xF6)));
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
                Equals(component.ResourceId, desired.ResourceId) &&
                Equals(component.TypeInTargetAssembly, desired.TypeInTargetAssembly))
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
