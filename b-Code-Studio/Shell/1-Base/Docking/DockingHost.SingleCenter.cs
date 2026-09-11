using System.Windows.Threading;
using AvalonDock.Layout;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 每一格只留一页（REQ-UI-096，1.20.1 起推广到每一格，REQ-UI-100）与拖出即隐藏（REQ-UI-098）。
///
/// 1.20.0 只管主文档区（顶栏）。用户真机拖出来的缺陷：把一页不是登记在中央的工具页拖进主窗口时，
/// 只有顶栏会顶掉旧页，侧边、底边的窗格照样攒多页——于是「工具窗口在主窗口位置的那一页」和主窗口页互相抢。
/// 用户拍板统一：侧边栏、底边栏、主文档区、浮窗里的每一格窗格，一律只留一页，其余隐藏。
///
/// 一格里出现第二页时留哪一页，按两种来路认：
/// <list type="bullet">
///   <item>程序路径（<c>Show</c> / <c>Dock</c> / 切场景 / 登记）：调用方说留哪一页，或者说「新来的不抢」；</item>
///   <item>用户拖放：布局变了之后比对上一次记下的「每页在哪一格」，这一格里新出现的就是拖进来的那一页。</item>
/// </list>
/// 被顶掉的页只是隐藏——场景不拥有页面（REQ-UI-094），隐藏状态记在当前场景的布局里。
/// </summary>
internal sealed partial class DockingHost
{
    /// <summary>上一次整理之后每一页所在的窗格。用户拖进来一页时，拿它认出谁是新来的。</summary>
    private Dictionary<string, ILayoutContainer> _pagePanes = new(StringComparer.OrdinalIgnoreCase);

    private bool _singlePagePending;

    /// <summary>
    /// 登记时没抢到位子、正在等位的页 → 占着那个位子的页。
    ///
    /// 新登记的页不抢位（留原来那一页），可原来那一页随后可能被场景当成外来页藏掉——
    /// 在 Minerva 场景里 Janus 与 Minerva 同一批热重载，Janus 的页先占了右栏，Minerva 的页落进去被藏，
    /// 场景再把 Janus 的页藏掉，右栏就空了，而 Minerva 自己的新页也没露面。所以占位的页被明确隐藏时，
    /// 等位的页回到这个位子（<see cref="ReturnSeatsOf"/>）。只在一份布局里有效：切场景、换布局就清空，
    /// 不会在别的场景里突然冒出一页。
    /// </summary>
    private readonly Dictionary<string, string> _waitingForSeat = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每一格窗格只留一页，返回被藏掉的页与它那一格留下的页。
    /// <paramref name="keepId"/> 所在的那一格留它；其次留 <paramref name="prefer"/> 里的页（切场景时刚露面的页）；
    /// 其余格子里，<paramref name="newcomersWin"/> 为 true 时留新来的页（用户拖进来、调用方刚放进去的），
    /// 为 false 时留原来就在那一格的页（登记、恢复布局不抢位）；都认不出时留选中页，再退回最后一页。
    /// 专注态换的是另一棵布局树，不处理。
    /// </summary>
    internal IReadOnlyList<(string Hidden, string Kept)> EnforceSinglePagePerPane(
        string? keepId = null,
        bool newcomersWin = true,
        IReadOnlyCollection<string>? prefer = null)
    {
        var displaced = new List<(string Hidden, string Kept)>();
        if (_maximizedId != null)
            return displaced;

        foreach (var pane in PagePanes().ToList())
        {
            var pages = pane.Children
                .OfType<LayoutContent>()
                .Where(page => page.ContentId is { } id && _byId.ContainsKey(id))
                .ToList();
            if (pages.Count <= 1)
                continue;

            var keep = PickSurvivor(pane, pages, keepId, newcomersWin, prefer);
            using (Suppress())
            {
                foreach (var page in pages)
                {
                    if (ReferenceEquals(page, keep))
                        continue;
                    try
                    {
                        // 不走公开的 Hide：被顶掉不等于被明确隐藏，不该把等在它身上的页放出来。
                        HideCore(page.ContentId!);
                        displaced.Add((page.ContentId!, keep.ContentId!));
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(LayoutSource, $"每格只留一页时隐藏 {page.ContentId} 失败: {ex.Message}");
                    }
                }

                if (keep.Parent != null)
                    keep.IsSelected = true;
            }

            _log.Info(LayoutSource, $"每格只留一页：保留 {keep.ContentId}，同一格另外 {pages.Count - 1} 页已隐藏");
        }

        RecordPagePanes();
        return displaced;
    }

    /// <summary>占着位子的 <paramref name="occupant"/> 被明确隐藏了：等它位子的页回来。</summary>
    private void ReturnSeatsOf(string occupant)
    {
        string? returned = null;
        foreach (var waiting in _waitingForSeat
                     .Where(pair => pair.Value.Equals(occupant, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _waitingForSeat.Remove(waiting);
            if (!_byId.ContainsKey(waiting) || ComputeState(waiting).Visible)
                continue;
            try
            {
                ShowCore(waiting);
                returned = waiting;
                _log.Info(LayoutSource, $"{occupant} 隐藏了，登记时等它位子的 {waiting} 露面");
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"{waiting} 回到 {occupant} 让出的位子失败: {ex.Message}");
            }
        }

        if (returned != null)
            EnforceSinglePagePerPane(returned);
    }

    /// <summary>主文档区、侧边与底边窗格、浮窗里的窗格。自动隐藏的边条不算格子。</summary>
    private IEnumerable<ILayoutContainer> PagePanes()
        => _manager.Layout.Descendents()
            .Where(element => element is LayoutAnchorablePane or LayoutDocumentPane)
            .Cast<ILayoutContainer>();

    private LayoutContent PickSurvivor(
        ILayoutContainer pane,
        IReadOnlyList<LayoutContent> pages,
        string? keepId,
        bool newcomersWin,
        IReadOnlyCollection<string>? prefer)
    {
        var named = pages.FirstOrDefault(page =>
            string.Equals(page.ContentId, keepId, StringComparison.OrdinalIgnoreCase));
        if (named != null)
            return named;

        var preferred = prefer == null
            ? null
            : pages.LastOrDefault(page => prefer.Contains(page.ContentId!, StringComparer.OrdinalIgnoreCase));
        if (preferred != null)
            return preferred;

        var byHistory = newcomersWin
            ? pages.LastOrDefault(page => !WasIn(page, pane))
            : pages.FirstOrDefault(page => WasIn(page, pane));
        return byHistory
               ?? pages.FirstOrDefault(page => page.IsSelected)
               ?? pages[^1];
    }

    private bool WasIn(LayoutContent page, ILayoutContainer pane)
        => _pagePanes.TryGetValue(page.ContentId!, out var was) && ReferenceEquals(was, pane);

    private void RecordPagePanes()
    {
        var next = new Dictionary<string, ILayoutContainer>(StringComparer.OrdinalIgnoreCase);
        foreach (var pane in PagePanes())
        {
            foreach (var page in pane.Children.OfType<LayoutContent>())
            {
                if (page.ContentId is { } id)
                    next.TryAdd(id, pane);
            }
        }

        _pagePanes = next;
    }

    private string? SelectedCenterId() => TryFindMainDocumentPane()?.SelectedContent?.ContentId;

    /// <summary>主文档区此刻的那一页：选中页，没有选中页时取第一页（刚恢复的窗格可能还没选）。</summary>
    private string? CurrentCenterId()
    {
        var pane = TryFindMainDocumentPane();
        return pane?.SelectedContent?.ContentId ?? pane?.Children.FirstOrDefault()?.ContentId;
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
                // 程序路径正在改布局：它收尾时自己会整理；这一拍的用户手势等它放手再看。
                ScheduleSinglePageCheck();
                return;
            }

            EnforceSinglePagePerPane();
        });
    }

    /// <summary>
    /// 拖出去没有落到停靠点的页（REQ-UI-098）：先摆回它的默认位置，再隐藏。
    ///
    /// 为什么不直接隐藏：AvalonDock 隐藏一页时记住的「上一个容器」是那个浮窗，
    /// 下次再显示它会回到一个已经关掉的浮窗里。先摆回默认位置，隐藏记住的就是停靠位——
    /// 从右栏常用页面点它，它回到该在的地方。摆回去的那一下不许顶掉任何一格原来那一页。
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

        EnforceSinglePagePerPane(keep, newcomersWin: false);
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 让一个藏着的工具页露面：显示、浮出、自动隐藏之前都走这里。
    ///
    /// 两件以前可以不管、现在必须管的事——每格只留一页之后，被藏起来的页多了很多：
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
