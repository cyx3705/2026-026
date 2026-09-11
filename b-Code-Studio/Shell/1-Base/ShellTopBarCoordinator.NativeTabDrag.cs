using System.Reflection;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Controls;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base;

/// <summary>
/// AvalonDock 页签自带的那条拖动一律卸掉（REQ-UI-098 第 2 条，1.20.1）。
///
/// 1.20.0 说「平时按页签只换页」，Aurora 自己的拖动会话确实只在标签态才起；可 AvalonDock 4.72.1 的页签
/// 在自己的 <c>OnMouseLeftButtonDown</c> 里另外「上膛」一条原生拖动——文档页签按住拖出页签行、
/// 工具页签按住拖出页签条，就调 <c>StartDraggingFloatingWindowForContent</c> 浮出去。那条路不经过 Aurora，
/// 于是落空也不收起，留下一个悬浮窗（用户真机撞到，问题 3）。
///
/// 为什么不在隧道阶段判 Handled 拦掉按下：试过（1.17.x），容器 TabItem 收不到冒泡的按下，页就换不了。
/// 为什么不用 <c>CanMove = false</c>：文档页签只对 <c>LayoutDocument</c> 查它，而中央页几乎全是工具页。
/// 所以让 AvalonDock 照常处理按下（换页、激活都归它），在冒泡回到停靠管理器时把它刚上的膛退掉——
/// 三个私有字段，按 4.72.1 的源码取名；取不到时只报一次警告，不影响换页。
/// 门禁 <c>ShellChromeContractTests.NativeTabDragIsDisarmedOnEveryPress</c> 守着字段名，升级 AvalonDock 时会先红。
/// </summary>
internal sealed partial class ShellTopBarCoordinator
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? DocumentTabArmed =
        typeof(LayoutDocumentTabItem).GetField("_isMouseDown", PrivateInstance);

    private static readonly FieldInfo? AnchorableTabArmed =
        typeof(LayoutAnchorableTabItem).GetField("_isMouseDown", PrivateInstance);

    private static readonly FieldInfo? AnchorableTabDragging =
        typeof(LayoutAnchorableTabItem).GetField("_draggingItem", BindingFlags.Static | BindingFlags.NonPublic);

    private bool _nativeDragWarned;

    internal static bool NativeTabDragHooksResolved
        => DocumentTabArmed != null && AnchorableTabArmed != null && AnchorableTabDragging != null;

    private void OnDockMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DisarmNativeTabDrag(e.OriginalSource as DependencyObject);
    }

    /// <summary>按在页签上的这一下，AvalonDock 已经换完页、上好了膛：退膛。</summary>
    internal void DisarmNativeTabDrag(DependencyObject? source)
    {
        var tab = FindAncestor<FrameworkElement>(source, IsRealPageTab);
        if (tab == null)
            return;

        if (!NativeTabDragHooksResolved)
        {
            if (!_nativeDragWarned)
            {
                _nativeDragWarned = true;
                _log.Warn(ChromeLogSource, "AvalonDock 页签的拖动字段取不到，页签按住拖出仍会浮出悬浮窗（REQ-UI-098）");
            }
            return;
        }

        if (tab is LayoutDocumentTabItem)
        {
            DocumentTabArmed!.SetValue(tab, false);
        }
        else
        {
            AnchorableTabArmed!.SetValue(tab, false);
            AnchorableTabDragging!.SetValue(null, null);
        }
    }
}
