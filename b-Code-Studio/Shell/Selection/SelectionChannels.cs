namespace HistoryAurora.Shell.Selection;

/// <summary>某个通道的选中行变了。<see cref="Row"/> 已经是新值。</summary>
public sealed class SelectionChannelChangedEventArgs(
    string channel,
    IReadOnlyDictionary<string, string>? row) : EventArgs
{
    public string Channel { get; } = channel;

    /// <summary>新的选中行；无选中时为 null。</summary>
    public IReadOnlyDictionary<string, string>? Row { get; } = row;
}

/// <summary>台账里的一条通道，供 <c>aurora.ui.channels</c> 回显。</summary>
public sealed record SelectionChannelState(
    string Channel,
    string Owner,
    string Origin,
    bool HasSelection,
    IReadOnlyList<string> ReferencedBy);

/// <summary>
/// 选择通道（REQ-UI-041）：表格把当前选中行发布到一个**具名通道**，控制面板按通道名取值。
///
/// **为什么不能用节点 id**：页面是一页一页渲染的，节点表（<c>PageRenderer.RenderState</c>）
/// 的作用域就是那一页。Janus 的项目表在中央的「项目总览」页、操作面板在左侧的「项目操作」页，
/// 两页各渲染一次，互相看不见对方的节点——「选中一行，另一页的按钮跟着可用」这条链路
/// 在页内作用域里根本表达不出来。通道是**界面级**的，因此天然跨页。
///
/// 通道是派生状态：声明随页面来，页面撤了跟着撤（<see cref="DropOwner"/>）。
/// 与页面注册协议「拉取而不缓存」同一条理由——宿主侧 <c>web.frontendcatalog</c> 那种
/// 只进不出的目录，改一次名就留下永久幽灵。
/// </summary>
public sealed class SelectionChannels
{
    /// <summary>动作占位符里的通道前缀：<c>{selection.&lt;通道&gt;.&lt;列&gt;}</c>。</summary>
    public const string ReferencePrefix = "selection.";

    private readonly Dictionary<string, (string Owner, string Origin)> _declared =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _rows =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>引用方 → 它引用的通道名。按引用方替换，因此重建面板不会越积越多。</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _references =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>选中行变化。跨页面的接线全部挂在这一个事件上。</summary>
    public event EventHandler<SelectionChannelChangedEventArgs>? Changed;

    /// <summary>
    /// 登记一个通道。**同名通道只认第一个声明方**：两张表往同一通道发布的话，
    /// 「按钮跟着哪张表走」就取决于建页顺序，而建页顺序不受任何东西保证。
    /// </summary>
    public bool TryDeclare(string? channel, string owner, string origin, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(channel))
        {
            error = "通道名为空";
            return false;
        }

        if (_declared.TryGetValue(channel, out var existing))
        {
            // 同一个节点重新渲染（模块热重载）不算冲突：来源相同，换的只是控件实例。
            if (string.Equals(existing.Origin, origin, StringComparison.OrdinalIgnoreCase))
                return true;

            error = $"通道 {channel} 已由 {existing.Origin} 声明，{origin} 的声明被忽略";
            return false;
        }

        _declared[channel] = (owner, origin);
        return true;
    }

    /// <summary>撤掉某个 owner 名下的全部通道。页面撤了通道就得跟着撤，不留幽灵。</summary>
    public void DropOwner(string owner)
    {
        foreach (var channel in _declared
                     .Where(pair => string.Equals(pair.Value.Owner, owner, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _declared.Remove(channel);
            if (_rows.Remove(channel))
                Changed?.Invoke(this, new SelectionChannelChangedEventArgs(channel, null));
        }
    }

    /// <summary>表格把当前选中行发上通道；无选中时传 null。</summary>
    public void Publish(string channel, IReadOnlyDictionary<string, string>? row)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return;

        if (row == null)
            _rows.Remove(channel);
        else
            _rows[channel] = row;

        Changed?.Invoke(this, new SelectionChannelChangedEventArgs(channel, row));
    }

    /// <summary>登记引用方用到了哪几个通道。按引用方**整体替换**，重建面板即自动清旧。</summary>
    public void Reference(string origin, IEnumerable<string> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var names = channels
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
            _references.Remove(origin);
        else
            _references[origin] = names;
    }

    /// <summary>当前选中行；无选中时为 null。</summary>
    public IReadOnlyDictionary<string, string>? Current(string channel)
        => _rows.GetValueOrDefault(channel);

    public bool HasSelection(string? channel)
        => channel is { Length: > 0 } && _rows.ContainsKey(channel);

    public bool IsDeclared(string? channel)
        => channel is { Length: > 0 } && _declared.ContainsKey(channel);

    /// <summary>通道台账，按通道名排序。</summary>
    public IReadOnlyList<SelectionChannelState> Snapshot()
        => _declared
            .Select(pair => new SelectionChannelState(
                pair.Key,
                pair.Value.Owner,
                pair.Value.Origin,
                _rows.ContainsKey(pair.Key),
                _references
                    .Where(r => r.Value.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.Key)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(state => state.Channel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 断链：引用了一个没有任何表格声明的通道。
    ///
    /// 这是本机制唯一会「安静地不工作」的形态——按钮永远灰着，与「还没选中」长得一模一样。
    /// 因此它必须出账，而不是等人去猜。判定只能在**整轮建页之后**做：
    /// 面板所在的页可能先于表格所在的页渲染，建时判会把正常情况报成断链。
    /// </summary>
    public IReadOnlyList<string> Dangling
        => _references
            .SelectMany(pair => pair.Value.Select(channel => (Origin: pair.Key, Channel: channel)))
            .Where(item => !_declared.ContainsKey(item.Channel))
            .Select(item => $"{item.Origin} → {item.Channel}（没有表格声明这个通道）")
            .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---------------------------------------------------------------- 引用文本

    public static bool IsReference(string? name)
        => name != null && name.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>拆 <c>selection.&lt;通道&gt;.&lt;列&gt;</c>。</summary>
    public static bool TrySplitReference(string? reference, out string channel, out string column)
    {
        channel = "";
        column = "";
        return IsReference(reference)
               && TrySplitBinding(reference![ReferencePrefix.Length..], out channel, out column);
    }

    /// <summary>
    /// 拆 <c>&lt;通道&gt;.&lt;列&gt;</c>（面板 <c>follows</c> 的写法，不带前缀）。
    ///
    /// **按最后一个点分**，因为通道名自己就带点（<c>janus.project</c>）——
    /// 按第一个点分会把 <c>janus.project.name</c> 读成通道 <c>janus</c> 加列 <c>project.name</c>，
    /// 而那个通道确实可能存在，于是错得毫无声响。列名不含点，最后一个点因此是确定的分界。
    /// </summary>
    public static bool TrySplitBinding(string? binding, out string channel, out string column)
    {
        channel = "";
        column = "";
        if (string.IsNullOrWhiteSpace(binding))
            return false;

        var cut = binding.LastIndexOf('.');
        if (cut <= 0 || cut == binding.Length - 1)
            return false;

        channel = binding[..cut];
        column = binding[(cut + 1)..];
        return true;
    }

    /// <summary>按 <c>&lt;通道&gt;.&lt;列&gt;</c> 取值；取不到（未选中或没这一列）返回 null。</summary>
    public string? Value(string channel, string column)
        => _rows.TryGetValue(channel, out var row) && row.TryGetValue(column, out var cell)
            ? cell
            : null;

    /// <summary>按 <c>selection.&lt;通道&gt;.&lt;列&gt;</c> 取值。</summary>
    public string? Resolve(string? reference)
        => TrySplitReference(reference, out var channel, out var column) ? Value(channel, column) : null;

    /// <summary>
    /// 把通道接进动作占位符的取值链：<c>{selection.*}</c> 走通道，其余仍按控件 id 走面板。
    /// 两个命名空间分得开，面板里就不必回避某个控件 id。
    /// </summary>
    public Func<string, string?> Extend(Func<string, string?> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        return name => IsReference(name) ? Resolve(name) : controls(name);
    }

    /// <summary>台账不可用时退回纯控件取值——{selection.*} 随之取不到值，动作整条拒绝执行。</summary>
    public static Func<string, string?> Chain(SelectionChannels? channels, Func<string, string?> controls)
        => channels?.Extend(controls) ?? controls;
}
