using System.Windows;
using System.Windows.Media;
using AvalonDock.Controls;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base;

internal sealed partial class PageDragCoordinator
{
    private static Size NormalizeEmbeddedSize(Size size)
        => double.IsFinite(size.Width) && size.Width > 0 &&
           double.IsFinite(size.Height) && size.Height > 0
            ? size
            : new Size(720, 520);

    private void ApplyFloatingWindowGeometry(
        LayoutFloatingWindowControl floating,
        FloatingDragContext context)
    {
        try
        {
            floating.WindowState = WindowState.Normal;
            FloatingWindowGeometry.PlaceWindow(
                floating,
                FloatingWindowGeometry.GetCursorPosition(),
                NormalizeEmbeddedSize(context.EmbeddedSize),
                context.AnchorOffset);
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn(ChromeLogSource, $"浮窗几何应用失败：{ex.Message}");
        }
    }

    private LayoutContent? FindLayoutContent(string id)
        => _manager.Layout.Descendents()
            .OfType<LayoutContent>()
            .FirstOrDefault(content =>
                content.ContentId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>窗格控件的选中项：可能是页签容器，也可能直接是模型。</summary>
    private static bool TryResolveSelectedItem(object? selectedItem, out string id)
    {
        var model = selectedItem switch
        {
            LayoutAnchorableTabItem { Model: LayoutContent anchorable } => anchorable,
            LayoutDocumentTabItem { Model: LayoutContent document } => document,
            LayoutContent content => content,
            _ => null,
        };
        id = model?.ContentId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(id);
    }

    private static bool ModelContainsPage(ILayoutElement? element, string id)
    {
        if (element is LayoutContent content &&
            content.ContentId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return element is ILayoutContainer container &&
               container.Children.Any(child => ModelContainsPage(child, id));
    }

    private static DependencyObject? GetParent(DependencyObject current)
        => current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private static T? FindAncestor<T>(DependencyObject? source, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is T match && (predicate == null || predicate(match)))
                return match;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }
}
