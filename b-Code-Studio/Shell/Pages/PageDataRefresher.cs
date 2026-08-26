using System.Windows.Threading;
using HistoryAurora.Shell.Selection;

namespace HistoryAurora.Shell.Pages;

/// <summary>台账里的一条取数绑定，供 <c>aurora.ui.refreshdata</c> 回显。</summary>
public sealed record PageDataBinding(string Owner, string Page, string Node, IReadOnlyList<string> Channels);

/// <summary>
/// 取数刷新台账（REQ-UI-044）。
///
/// **为什么需要它**：1.8.15 之前，表格与泳道图的取数只在控件 <c>Loaded</c> 时跑一次，
/// 此后没有任何再取的入口。两个后果都在 Janus 上真实发生：
/// 「刷新」按钮只能把指令打到控制台，界面上那张表一动不动；
/// 「分支历史」这类**依赖当前选中项目**的表，选中一换就成了过期数据，
/// 而过期数据与新数据长得一模一样。
///
/// 因此取数绑定要能被再次触发，触发源有两个：
/// <list type="bullet">
///   <item>取数参数里引用的**选择通道**变了——自动重取；</item>
///   <item>有人显式调 <c>aurora.ui.refreshdata</c>——按页或按节点重取。</item>
/// </list>
///
/// 绑定是派生状态，随页面来、随页面撤（<see cref="DropOwner"/>），与选择通道同一条理由。
/// </summary>
public sealed class PageDataRefresher
{
    /// <summary>
    /// 跟随选中的重取要消抖，时长取这个值。
    ///
    /// 一次选中变化会同时触发这一页上全部引用了该通道的取数。Janus 上是三条：
    /// 图谱、分支历史、规则落地状态，其中落地状态每次跑两条 `git ls-files`。
    ///
    /// **实测的是**：键盘翻项目时选中变化间隔约 450~600ms，每条取数 120~220ms 返回，
    /// 这个速度下它是跟得上的，**没有观察到堆积**。所以这里是防御，不是在修一个已发生的故障——
    /// 按住方向键不放、或者刷新后程序重设选中时，间隔会短得多，那时三条 Git 取数逐次叠加。
    ///
    /// 250ms 取的是「明显是连续动作」这一档，比实测的键盘间隔小，因此正常翻页的手感不变。
    /// </summary>
    private static readonly TimeSpan FollowDelay = TimeSpan.FromMilliseconds(250);

    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _pendingChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer? _debounce;

    public PageDataRefresher(SelectionChannels? channels = null)
    {
        // 只订阅一次，按条目上记着的通道名分派。每个绑定各订一次的话，
        // 撤页时漏解一个就会留下一个指着废弃控件、还在发指令的处理器。
        if (channels == null)
            return;

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = FollowDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            var channelNames = _pendingChannels.ToList();
            _pendingChannels.Clear();
            Refresh(entry => channelNames.Any(entry.Follows));
        };

        channels.Changed += (_, e) =>
        {
            _pendingChannels.Add(e.Channel);
            // 重启计时器：连续的选中变化因此合并成最后一次，而不是各取各的。
            _debounce.Stop();
            _debounce.Start();
        };
    }

    /// <summary>
    /// 登记一条取数绑定。<paramref name="channels"/> 是取数参数里引用到的通道名，
    /// 空表示这条取数与选中无关，只能被显式刷新。
    /// </summary>
    public void Register(
        string owner,
        string page,
        string? node,
        IReadOnlyList<string> channels,
        Func<Task> reload)
    {
        ArgumentNullException.ThrowIfNull(reload);
        _entries.Add(new Entry(owner, page, node ?? "", channels, reload));
    }

    /// <summary>撤掉某个 owner 名下的全部绑定。页面撤了还留着，就是对着废弃控件发指令。</summary>
    public void DropOwner(string owner)
    {
        _entries.RemoveAll(entry =>
            string.Equals(entry.Owner, owner, StringComparison.OrdinalIgnoreCase));

        // 排着队的那一轮也要作废：它指向的控件可能正是刚被撤掉的那些。
        if (_entries.Count == 0)
        {
            _pendingChannels.Clear();
            _debounce?.Stop();
        }
    }

    /// <summary>当前绑定，按页与节点排序。</summary>
    public IReadOnlyList<PageDataBinding> Snapshot()
        => _entries
            .Select(entry => new PageDataBinding(entry.Owner, entry.Page, entry.Node, entry.Channels))
            .OrderBy(binding => binding.Page, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binding => binding.Node, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 按页 / 按节点重取；两个都不给就是全部。**不消抖**——这是人按下按钮的那一刻，
    /// 延迟 250ms 才动会被感知成"卡了一下"。消抖只对跟随选中的那条路有意义。
    ///
    /// 返回被触发的条数：「刷新了 0 条」与「刷新完成」必须能分开，
    /// 否则按钮点了没反应就无从查起。
    /// </summary>
    public int Refresh(string? page, string? node)
        => Refresh(entry =>
            (page == null || string.Equals(entry.Page, page, StringComparison.OrdinalIgnoreCase))
            && (node == null || string.Equals(entry.Node, node, StringComparison.OrdinalIgnoreCase)));

    private int Refresh(Func<Entry, bool> match)
    {
        // 先拍一份快照再跑：取数是异步的，回来时可能已经有人重载了页面，
        // 而在遍历过程中被改的集合会当场抛。
        var targets = _entries.Where(match).ToList();
        foreach (var entry in targets)
            _ = entry.Reload();
        return targets.Count;
    }

    private sealed record Entry(
        string Owner,
        string Page,
        string Node,
        IReadOnlyList<string> Channels,
        Func<Task> Reload)
    {
        public bool Follows(string channel)
            => Channels.Contains(channel, StringComparer.OrdinalIgnoreCase);
    }
}
