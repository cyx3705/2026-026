using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Docking;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryAurora.Shell;

internal sealed partial class ShellTopBarCoordinator : IDisposable
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

    private void UpdateDocumentPaneChrome(LayoutDocumentPaneControl pane)
    {
        pane.ApplyTemplate();
        var selected = (pane.Model as LayoutDocumentPane)?.SelectedContent as LayoutContent;
        var floating = selected?.ContentId is { Length: > 0 } id
            ? FindFloatingWindow(id)
            : null;

        if (pane.Template.FindName("ShellChromeHost", pane) is ContentControl chromeHost)
            chromeHost.Visibility = floating == null ? Visibility.Visible : Visibility.Collapsed;
        if (pane.Template.FindName("FloatingDocumentMaxRestore", pane) is Button button)
        {
            button.Visibility = floating == null ? Visibility.Collapsed : Visibility.Visible;
            button.ToolTip = floating?.WindowState == WindowState.Maximized
                ? "向下还原"
                : "最大化";
        }
        if (pane.Template.FindName("FloatingDocumentMaxRestoreIcon", pane) is
            System.Windows.Shapes.Path icon &&
            pane.TryFindResource(floating?.WindowState == WindowState.Maximized
                ? "Aurora.Icon.Restore"
                : "Aurora.Icon.Maximize") is Geometry geometry)
        {
            icon.Data = geometry;
        }
    }

    private static bool TryResolveTabPageId(DependencyObject? source, out string id)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (TryGetTabModel(current, out var model) && TryGetContentId(model, out id))
                return true;
        }

        id = string.Empty;
        return false;
    }

    private static bool TryGetTabModel(DependencyObject current, out LayoutContent model)
    {
        if (current is LayoutAnchorableTabItem { Model: LayoutContent anchorable })
        {
            model = anchorable;
            return true;
        }

        if (current is LayoutDocumentTabItem { Model: LayoutContent document })
        {
            model = document;
            return true;
        }

        model = null!;
        return false;
    }

    private static bool TryResolveSelectedItem(object? selectedItem, out string id)
    {
        if (selectedItem is DependencyObject dependency &&
            TryGetTabModel(dependency, out var tabModel) && TryGetContentId(tabModel, out id))
        {
            return true;
        }

        if (selectedItem is LayoutContent selected && TryGetContentId(selected, out id))
            return true;

        id = string.Empty;
        return false;
    }

    private static bool TryGetContentId(LayoutContent content, out string id)
    {
        id = content.ContentId ?? string.Empty;
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

    private static bool IsRealPageTab(FrameworkElement element)
        => element is LayoutAnchorableTabItem or LayoutDocumentTabItem;

    // ================================================================ 标题栏
    //
    // 以下三个判定**只在标题栏这一段可视树上生效**，向上走到标题栏容器为止。
    // 页面内容里有什么控件，与这里无关，不得混进同一个谓词——
    // 1.8.9 正是把两边合并成一条 `IsInteractive` 之后出的事：为了让页面里的
    // AuroraOptionBox（继承 Selector）被认成可交互件，把判据从 ComboBox 放宽成
    // Selector；而 AvalonDock 的窗格控件本身就是 TabControl，也就是 Selector，
    // 于是标题栏上任何一点向上走都会撞见它，整条标题栏被判成「可交互」，
    // 主窗口从此拖不动。边界必须在标题栏容器处断掉，而不是靠列举类型去躲。

    /// <summary>窗格模板里标题栏容器的两个标记(见 AuroraDocking.xaml)。</summary>
    private static bool IsPaneHeaderTag(object? tag)
        => Equals(tag, "ShellPaneHeader") || Equals(tag, "FocusedShellPaneHeader");

    /// <summary>命中点落在标题栏容器内。落在页面内容里的一律不算。</summary>
    private static bool IsPaneHeaderSource(DependencyObject? source)
        => FindPaneHeader(source) != null;

    private static FrameworkElement? FindPaneHeader(DependencyObject? source)
        => FindAncestor<FrameworkElement>(source, element => IsPaneHeaderTag(element.Tag));

    /// <summary>
    /// 标题栏里那些「点击归控件自己」的东西：按钮、菜单、页签、输入框。
    /// 命中其中之一就不该顺手把窗口拖走。
    ///
    /// 走到标题栏容器为止：越过它就是窗格本体，而窗格本体是 TabControl。
    /// 没找到标题栏容器时按「不是标题栏」处理，交给 IsPaneHeaderSource 拦。
    /// </summary>
    private static bool IsInteractiveInPaneHeader(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is FrameworkElement element && IsPaneHeaderTag(element.Tag))
                return false;

            if (current is ButtonBase or MenuItem or TextBoxBase or ComboBox or
                TabItem or LayoutAnchorableTabItem or LayoutDocumentTabItem)
            {
                return true;
            }
        }

        return false;
    }

    // ================================================================ 页面内容
    //
    // 页面内容里的控件判定与标题栏完全分开。这里可以按需要列举页面用得上的
    // 交互控件，加一种控件不会影响标题栏的拖动判定。

    /// <summary>
    /// 页面/页签上「点击归控件自己」的控件。
    ///
    /// 逐个列举而不是写 <c>Selector</c>：Selector 会把 TabControl、ListBox 一并框进来，
    /// 而 AvalonDock 的窗格控件就是 TabControl，写宽一格就等于把整块页面判成可交互。
    /// </summary>
    private static bool IsInteractiveCommandControl(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ButtonBase or MenuItem or TextBoxBase or ComboBox or Widgets.AuroraOptionBox)
                return true;
        }

        return false;
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

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    }
}
