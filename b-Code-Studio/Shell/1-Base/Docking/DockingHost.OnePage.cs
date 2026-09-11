using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 一格一页（REQ-UI-100，1.20.2 改写）：侧边、底边、主文档区、浮窗里的每一格窗格，任何时刻只放一页。
///
/// 1.20.0 / 1.20.1 的做法是「先让页挤进同一格，再挑一页留下、其余隐藏」：登记时新来的藏着等位
/// （等位账），用户拖进来时比对上一次记下的「每页在哪一格」认新来的，场景切换时再按偏好挑。
/// 真机上它会在几页之间来回抢位——一格里允许存在多页，只是只显示一页。1.20.2 把这整条链删掉：
/// <list type="bullet">
///   <item>程序路径放一页进一格，先把这一格原来那一页藏起来，再放进去（<see cref="PutInPane"/>）。
///         「不抢位」的放法（登记、恢复、摆回默认位置）是这一格有页时新来的直接藏起来，不记账、不等位；</item>
///   <item>唯一绕不开的是 AvalonDock 自己的「放进这一格」停靠点：它把拖来的页插成同一格的第二页。
///         布局一变就收拾（<see cref="EvictExtraPages"/>）：留刚从浮窗落下来的那一页。</item>
/// </list>
/// 没有页签、没有页签切换：被顶掉的页只是隐藏，要回来走右栏常用页面或 <c>aurora.ui.show</c>。
/// </summary>
internal sealed partial class DockingHost
{
    private bool _singlePagePending;

    /// <summary>
    /// 此刻浮着的页（含拖动途中刚建出来的浮窗里的页）。用户把浮窗落进一格时，新来的就是它。
    /// 只是「最近一次看到的浮窗内容」，每次收拾完、每次重建基线都按布局树重取，不跨布局累积。
    /// </summary>
    private HashSet<string> _floatingPages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把一页放进一格。<paramref name="takeSeat"/> 为 true 时这一格原来那一页被藏起来；
    /// 为 false 时（不抢位）这一格已经有页，新来的直接藏起来——AvalonDock 记住的「上一个容器」
    /// 就是这一格，之后显示它，它回到这里并顶掉当时那一页。
    /// </summary>
    /// <returns>这一页此刻露面。</returns>
    private bool PutInPane(ILayoutContainer pane, LayoutAnchorable page, bool takeSeat)
    {
        var occupied = PagesIn(pane).Any(other => !ReferenceEquals(other, page));
        if (occupied && takeSeat)
            EvictAllBut(pane, page);

        if (!ReferenceEquals(page.Parent, pane))
        {
            Detach(page);
            switch (pane)
            {
                case LayoutAnchorablePane anchorablePane:
                    anchorablePane.Children.Add(page);
                    break;
                case LayoutDocumentPane documentPane:
                    documentPane.Children.Add(page);
                    break;
                default:
                    throw new InvalidOperationException($"不支持的窗格类型: {pane.GetType().Name}");
            }
        }

        page.CanAutoHide = true;
        if (occupied && !takeSeat)
        {
            HidePage(page);
            return false;
        }

        _hiddenCenterIds.Remove(page.ContentId ?? string.Empty);
        page.IsSelected = true;
        return true;
    }

    /// <summary>这一格里除 <paramref name="keep"/> 以外的页一律藏起来。</summary>
    private void EvictAllBut(ILayoutContainer pane, LayoutContent keep)
    {
        foreach (var other in PagesIn(pane).Where(page => !ReferenceEquals(page, keep)).ToList())
        {
            HidePage(other);
            _log.Info(LayoutSource, $"一格一页：{keep.ContentId} 进来，{other.ContentId} 已隐藏");
        }
    }

    /// <summary>藏起一页并记住它的位置。被顶掉与被明确隐藏走同一条路，没有「等位」这回事。</summary>
    private void HidePage(LayoutAnchorable page)
    {
        if (page.IsHidden)
            return;
        var id = page.ContentId!;
        RememberCurrentPlacement(id);
        var wasCenter = IsHostedInDocumentPane(page);
        page.Hide();
        if (wasCenter)
            _hiddenCenterIds.Add(id);
    }

    /// <summary>一格里已登记的页。</summary>
    private IEnumerable<LayoutAnchorable> PagesIn(ILayoutContainer pane)
        => pane.Children
            .OfType<LayoutAnchorable>()
            .Where(page => page.ContentId is { } id && _byId.ContainsKey(id));

    /// <summary>主文档区、侧边与底边窗格、浮窗里的窗格。自动隐藏的边条不算格子。</summary>
    private IEnumerable<ILayoutContainer> PagePanes()
        => _manager.Layout.Descendents()
            .Where(element => element is LayoutAnchorablePane or LayoutDocumentPane)
            .Cast<ILayoutContainer>();

    /// <summary>
    /// 布局里出现了一格多页（AvalonDock 的「放进这一格」停靠点，或 1.20.1 及以前存下的布局）：
    /// 每格留一页，其余隐藏。留哪一页按先后：<paramref name="keepId"/>；刚从浮窗落下来的；
    /// 活动页；这一格的选中页；最后一页。
    /// 专注态换的是另一棵布局树，不处理。
    /// </summary>
    internal void EvictExtraPages(string? keepId = null)
    {
        if (_maximizedId != null)
            return;

        var active = _manager.Layout.ActiveContent;
        using (Suppress())
        {
            foreach (var pane in PagePanes().ToList())
            {
                var pages = PagesIn(pane).ToList();
                if (pages.Count <= 1)
                    continue;

                var keep = pages.FirstOrDefault(page =>
                               string.Equals(page.ContentId, keepId, StringComparison.OrdinalIgnoreCase))
                           ?? pages.LastOrDefault(page => _floatingPages.Contains(page.ContentId!))
                           ?? pages.FirstOrDefault(page => ReferenceEquals(page, active))
                           ?? pages.FirstOrDefault(page => page.IsSelected)
                           ?? pages[^1];
                EvictAllBut(pane, keep);
                if (keep.Parent != null)
                    keep.IsSelected = true;
            }
        }

        RecordFloatingPages();
    }

    private void RecordFloatingPages()
        => _floatingPages = _manager.Layout.FloatingWindows
            .SelectMany(window => window.Descendents().OfType<LayoutContent>())
            .Select(content => content.ContentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>拖动途中新建的浮窗：里面的页记为「浮着」，落进哪一格它就留在哪一格。</summary>
    private void OnFloatingWindowControlCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        foreach (var content in e.LayoutFloatingWindowControl.Model.Descendents().OfType<LayoutContent>())
        {
            if (!string.IsNullOrWhiteSpace(content.ContentId))
                _floatingPages.Add(content.ContentId);
        }
    }

    /// <summary>用户手势改了布局：等这一拍的布局事件都落定，再看哪一格里多了一页。</summary>
    private void ScheduleSinglePageCheck()
    {
        if (_singlePagePending)
            return;
        _singlePagePending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _singlePagePending = false;
            if (_maximizedId != null)
                return;
            if (_suppress > 0)
            {
                // 程序路径正在改布局：它自己不会留下一格多页；这一拍的用户手势等它放手再看。
                ScheduleSinglePageCheck();
                return;
            }

            EvictExtraPages();
        });
    }

    /// <summary>
    /// 拖出去没有落到停靠点的页（REQ-UI-098）：摆回它的默认位置并隐藏，不顶掉那里原来的页。
    ///
    /// 为什么不直接隐藏：AvalonDock 隐藏一页时记住的「上一个容器」是那个浮窗，
    /// 下次再显示它会回到一个已经关掉的浮窗里。先摆回默认位置，隐藏记住的就是停靠位。
    /// </summary>
    public void ParkHidden(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var anchorable = MoveToAnchorable(_byId[id]);
            PlaceAtDefault(anchorable, takeSeat: false);
            if (!anchorable.IsHidden)
                HidePage(anchorable);
            _manager.Layout.CollectGarbage();
            EnsureCentralWorkspace();
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按登记时的默认位置放一页。</summary>
    private void PlaceAtDefault(LayoutAnchorable anchorable, bool takeSeat)
    {
        var descriptor = _byId[anchorable.ContentId!];
        PlaceAtSide(anchorable, descriptor.DefaultSide, descriptor.DefaultRatio, descriptor.DefaultTabTarget, takeSeat);
    }

    /// <summary>
    /// 让一个藏着的工具页露面：显示、浮出、自动隐藏之前都走这里。
    ///
    /// AvalonDock 按「上一个容器」放回它；那个容器可能已经不在布局树里了（主文档区被换成了新窗格、
    /// 浮窗关掉了）。还记着旧容器时放进去等于凭空消失，<c>CollectGarbage</c> 已把旧容器清掉时
    /// 它自作主张挂到侧边窗格上——所以露面之后不在树里、或者本是中央页却没回到文档区，都按默认位置重新落位。
    ///
    /// <paramref name="takeSeat"/> 为 false 时（浮出、自动隐藏）不顶掉任何一格：要重新落位就单开一格，
    /// 反正它马上就要离开这里。
    /// </summary>
    private void RevealAnchorable(LayoutAnchorable anchorable, string id, bool takeSeat)
    {
        var wasCenter = _hiddenCenterIds.Remove(id);
        if (anchorable.IsHidden)
            anchorable.Show();
        if (anchorable.Root != null && !(wasCenter && !IsHostedInDocumentPane(anchorable)))
        {
            if (takeSeat && anchorable.Parent is ILayoutContainer pane)
                EvictAllBut(pane, anchorable);
            return;
        }

        if (anchorable.Parent is ILayoutContainer lost)
            lost.RemoveChild(anchorable);

        _log.Info(LayoutSource, $"窗口 {id} 原来的容器已不在布局里，按默认位置放回");
        if (!takeSeat)
        {
            EnsureSideRootPanel().Children.Add(new LayoutAnchorablePane(anchorable));
            return;
        }

        if (wasCenter)
            PutInPane(FindMainDocumentPane(), anchorable, takeSeat: true);
        else
            PlaceAtDefault(anchorable, takeSeat: true);
    }
}
