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

        // 回退色取本字典树里 Aurora 自己的有名令牌（AuroraTokens*.xaml），不在代码里写颜色（1.26.0，REQ-UI-128）。
        // 按字符串键取不受 ComponentResourceKey 在可回收上下文里解析失败的影响——那正是本修复要绕开的坑。
        // 按钮底与星形底**必须透明**（缺键时 AvalonDock 会退回 VS 调色板的实色）；透明是结构值，不是配色。
        var fallbacks = new List<(string Name, object Key, Brush Fallback)>
        {
            (nameof(ResourceKeys.DockingButtonBackgroundBrushKey), ResourceKeys.DockingButtonBackgroundBrushKey, Brushes.Transparent),
            (nameof(ResourceKeys.DockingButtonStarBackgroundBrushKey), ResourceKeys.DockingButtonStarBackgroundBrushKey, Brushes.Transparent),
        };
        foreach (var (name, key, token) in Fallbacks())
        {
            if (FindResource(theme, token) is Brush brush)
                fallbacks.Add((name, key, brush));
        }

        var changed = new List<string>();
        EnsureThemeDictionary(theme, changed, fallbacks);
        return changed;
    }

    private static void EnsureThemeDictionary(
        ResourceDictionary dictionary,
        ICollection<string> changed,
        IReadOnlyList<(string Name, object Key, Brush Fallback)> fallbacks)
    {
        foreach (var (name, key, fallback) in fallbacks)
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
            EnsureThemeDictionary(merged, changed, fallbacks);
    }

    /// <summary>
    /// 需要兜底的有色 AvalonDock 键 → 对应的 Aurora 令牌名。预览框填充由 <see cref="PutAlways"/> 固定为透明。
    /// </summary>
    private static IEnumerable<(string Name, object Key, string Token)> Fallbacks()
    {
        yield return (
            nameof(ResourceKeys.DockingButtonForegroundBrushKey),
            ResourceKeys.DockingButtonForegroundBrushKey,
            "Aurora.Brush.DockTarget");
        yield return (
            nameof(ResourceKeys.DockingButtonForegroundArrowBrushKey),
            ResourceKeys.DockingButtonForegroundArrowBrushKey,
            "Aurora.Brush.DockTargetArrow");
        yield return (
            nameof(ResourceKeys.DockingButtonStarBorderBrushKey),
            ResourceKeys.DockingButtonStarBorderBrushKey,
            "Aurora.Brush.DockTargetHalo");
        yield return (
            nameof(ResourceKeys.PreviewBoxBorderBrushKey),
            ResourceKeys.PreviewBoxBorderBrushKey,
            "Aurora.Brush.DockTarget");
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
