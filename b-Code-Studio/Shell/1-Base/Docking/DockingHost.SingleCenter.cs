using System.Windows.Threading;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 顶栏只留一页（REQ-UI-096）与拖出即隐藏（REQ-UI-098）。
///
/// 顶栏最终要退役，这一轮先不再允许它里面有多个页签：主文档区一旦出现第二页，
/// 只留**最后进来的那一页**，其余隐藏。「最后进来」按两种来路认：
/// <list type="bullet">
///   <item>程序路径（<c>Show</c> / <c>Dock</c> / 切场景 / 登记）：调用方自己说留哪一页；</item>
///   <item>用户拖放：布局变了之后比对上一次记下的主文档区成员，新出现的就是拖进来的那一页。</item>
/// </list>
/// 被顶掉的页只是隐藏——场景不拥有页面（REQ-UI-094），隐藏状态记在当前场景的布局里。
/// </summary>
internal sealed partial class DockingHost
{
    /// <summary>上一次整理之后主文档区里的页。用户拖进来一页时，拿它认出谁是新来的。</summary>
    private HashSet<string> _centerMembers = new(StringComparer.OrdinalIgnoreCase);

    private bool _singleCenterPending;

    /// <summary>
    /// 主文档区只留一页。<paramref name="keepId"/> 不在区里时，依次退回：新来的页 → 选中页 → 最后一页。
    /// 专注态换的是另一棵布局树，不处理。
    /// </summary>
    internal void EnforceSingleCenterPage(string? keepId = null)
    {
        if (_maximizedId != null)
            return;
        var pane = TryFindMainDocumentPane();
        if (pane == null)
            return;

        var children = pane.Children.ToList();
        if (children.Count > 1)
        {
            var keep = children.FirstOrDefault(child =>
                           string.Equals(child.ContentId, keepId, StringComparison.OrdinalIgnoreCase))
                       ?? children.LastOrDefault(child =>
                           child.ContentId != null && !_centerMembers.Contains(child.ContentId))
                       ?? pane.SelectedContent
                       ?? children[^1];

            using (Suppress())
            {
                foreach (var child in children)
                {
                    if (ReferenceEquals(child, keep) || child.ContentId is not { } id || !_byId.ContainsKey(id))
                        continue;
                    try
                    {
                        Hide(id);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(LayoutSource, $"顶栏只留一页时隐藏 {id} 失败: {ex.Message}");
                    }
                }

                if (keep.Parent != null)
                    keep.IsSelected = true;
            }

            _log.Info(LayoutSource, $"顶栏只留一页：保留 {keep.ContentId}，另外 {children.Count - 1} 页已隐藏");
        }

        _centerMembers = pane.Children
            .Select(child => child.ContentId)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? SelectedCenterId() => TryFindMainDocumentPane()?.SelectedContent?.ContentId;

    /// <summary>主文档区此刻的那一页：选中页，没有选中页时取第一页（刚恢复的窗格可能还没选）。</summary>
    private string? CurrentCenterId()
    {
        var pane = TryFindMainDocumentPane();
        return pane?.SelectedContent?.ContentId ?? pane?.Children.FirstOrDefault()?.ContentId;
    }

    /// <summary>用户手势改了布局：等这一拍的布局事件都落定，再看主文档区里是不是多了一页。</summary>
    private void ScheduleSingleCenterCheck()
    {
        if (_singleCenterPending)
            return;
        _singleCenterPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _singleCenterPending = false;
            if (_suppress > 0 || _maximizedId != null)
                return;
            EnforceSingleCenterPage();
        });
    }

    /// <summary>
    /// 拖出去没有落到停靠点的页（REQ-UI-098）：先摆回它的默认位置，再隐藏。
    ///
    /// 为什么不直接隐藏：AvalonDock 隐藏一页时记住的「上一个容器」是那个浮窗，
    /// 下次再显示它会回到一个已经关掉的浮窗里。先摆回默认位置，隐藏记住的就是停靠位——
    /// 从右栏常用页面点它，它回到该在的地方。摆回去的那一下不许顶掉顶栏原来那一页。
    /// </summary>
    public void ParkHidden(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        var keep = SelectedCenterId();
        using (Suppress())
        {
            ResetWindowCore(id);
            _manager.Layout.CollectGarbage();
            Hide(id);
        }

        EnforceSingleCenterPage(keep);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 让一个藏着的工具页露面：显示、浮出、自动隐藏之前都走这里。
    ///
    /// 两件以前可以不管、现在必须管的事——顶栏只留一页之后，被藏起来的中央页多了很多：
    /// <list type="bullet">
    ///   <item>「中央区隐藏」那笔账要勾掉，否则状态一直报隐藏，浮出去也报「没浮」；</item>
    ///   <item>AvalonDock 按「上一个容器」放回它，而那个容器可能已经不在布局树里了
    ///         （主文档区被换成了新窗格、浮窗关掉了）。两种下场都不对：还记着旧容器时放进去等于凭空消失；
    ///         <c>CollectGarbage</c> 已把旧容器清掉时它自作主张挂到侧边窗格上。
    ///         所以露面之后不在树里、或者本是中央页却没回到文档区，都按默认位置重新落位。</item>
    /// </list>
    /// </summary>
    private void RevealAnchorable(LayoutAnchorable anchorable, string id)
    {
        var wasCenter = _hiddenCenterIds.Remove(id);
        if (anchorable.IsHidden)
            anchorable.Show();
        if (anchorable.Root != null && !(wasCenter && !IsHostedInDocumentPane(anchorable)))
            return;

        if (anchorable.Parent is ILayoutContainer lost)
            lost.RemoveChild(anchorable);

        var descriptor = _byId[id];
        var center = wasCenter
                     || descriptor.DefaultSide == DockSide.Center
                     || descriptor.DefaultSide == DockSide.Tab && IsCenterTabTarget(descriptor.DefaultTabTarget);
        if (center)
            ShowAnchorableAsCenterPage(anchorable);
        else
            PlaceAtSide(anchorable, descriptor.DefaultSide, descriptor.DefaultRatio, descriptor.DefaultTabTarget);
        _log.Info(LayoutSource, $"窗口 {id} 原来的容器已不在布局里，按默认位置放回");
    }
}
