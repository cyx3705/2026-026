using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// 界面看到的指令目录（1.29.0，宿主 6.0.0 统一契约）：只读。
/// </summary>
/// <remarks>
/// <para>
/// 进程内装载时读宿主：<c>vulcan.command.revision</c> 不变就用缓存，变了才重拉 <c>vulcan.command.list</c>；
/// 宿主发 <c>vulcan.catalog.changed</c> 时由装配方调 <see cref="Invalidate"/>，<see cref="Changed"/> 随之触发。
/// 此前界面直接挂宿主注册表的 C# 事件、按宿主内部类型取描述符。
/// </para>
/// <para>
/// 独立运行（组件画廊、契约测试）时读本仓的 <see cref="CommandTable"/>，按宿主模块API的同一套规则投影。
/// </para>
/// </remarks>
public sealed class ShellCatalog
{
    private readonly Func<long> _revision;
    private readonly Func<IReadOnlyList<CommandInfo>?> _load;
    private readonly object _gate = new();
    private long _loadedRevision = long.MinValue;
    private IReadOnlyList<CommandInfo> _commands = [];
    private Dictionary<string, CommandInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    private ShellCatalog(Func<long> revision, Func<IReadOnlyList<CommandInfo>?> load)
    {
        _revision = revision;
        _load = load;
    }

    /// <summary>目录变了（宿主事件或本表登记）；在发出方线程上触发。</summary>
    public event Action? Changed;

    /// <summary>读宿主目录。两条都是只读、同步完成的宿主指令，走安静执行。</summary>
    public static ShellCatalog FromHost(ICommandBus host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new ShellCatalog(
            () =>
            {
                var result = host.InvokeAsync("vulcan.command.revision", "").GetAwaiter().GetResult();
                return result.Success && result.Data is JsonElement data
                       && data.TryGetProperty("revision", out var value) && value.TryGetInt64(out var parsed)
                    ? parsed
                    : -1;
            },
            () =>
            {
                var listed = host.InvokeAsync("vulcan.command.list", "").GetAwaiter().GetResult();
                if (!listed.Success || listed.Data is not JsonElement { ValueKind: JsonValueKind.Array } rows)
                    return null;
                return rows.EnumerateArray().Select(CommandInfo.FromJson).OfType<CommandInfo>().ToList();
            });
    }

    /// <summary>读本仓的指令表（独立运行与测试）。</summary>
    public static ShellCatalog FromTable(CommandTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var catalog = new ShellCatalog(
            () => table.Revision,
            () => table.All().Select(descriptor => CommandInfo.FromDescriptor(descriptor, table.GetSource(descriptor.Name))).ToList());
        table.Changed += catalog.Invalidate;
        return catalog;
    }

    /// <summary>只丢缓存、不通知：下一次读取时重拉（单模块页面失效时用，免得触发整轮界面发现）。</summary>
    public void Expire()
    {
        lock (_gate)
            _loadedRevision = long.MinValue;
    }

    /// <summary>丢掉缓存并通知订阅方；下一次读取时重拉。</summary>
    public void Invalidate()
    {
        lock (_gate)
            _loadedRevision = long.MinValue;
        Changed?.Invoke();
    }

    public IReadOnlyList<CommandInfo> All()
    {
        Refresh();
        lock (_gate)
            return _commands;
    }

    public bool TryGet(string name, out CommandInfo command)
    {
        Refresh();
        lock (_gate)
            return _byName.TryGetValue(name?.Trim() ?? "", out command!);
    }

    public string GetSource(string name) => TryGet(name, out var command) ? command.Source : "framework";

    public string GetDomain(string name) => TryGet(name, out var command) ? command.Domain : CommandNames.LegacyDomain(name);

    public string GetCommandClass(string name)
        => TryGet(name, out var command) ? command.CommandClass : CommandNames.LegacyClass(name);

    /// <summary>当前全部指令域，按序。</summary>
    public IReadOnlyList<string> Domains()
        => All().Select(command => command.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToList();

    public bool IsRegisteredDomain(string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
           && All().Any(command => command.Domain.Equals(candidate.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>未知指令的候选：编辑距离 ≤ 2，或同域全部动作；最多 5 条（与宿主 vulcan.command.suggest 同一规则）。</summary>
    public IReadOnlyList<string> Suggest(string unknownName)
    {
        var candidates = new List<(string Name, int Distance)>();
        var dot = unknownName.IndexOf('.');
        var domain = dot > 0 ? unknownName[..(dot + 1)] : null;
        foreach (var name in All().Select(command => command.Name))
        {
            var distance = Levenshtein(unknownName.ToLowerInvariant(), name);
            if (distance <= 2)
                candidates.Add((name, distance));
            else if (domain != null && name.StartsWith(domain, StringComparison.OrdinalIgnoreCase))
                candidates.Add((name, 3));
        }

        return candidates.OrderBy(item => item.Distance).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Name).Take(5).ToList();
    }

    /// <summary>
    /// 两次问版本号的最短间隔。控制台逐行按指令名归类，刷屏时一秒要查上千次；
    /// 目录真变了宿主会发 <c>vulcan.catalog.changed</c>（经 <see cref="Invalidate"/> 立即作废缓存），这里只是兜底。
    /// </summary>
    private static readonly TimeSpan RevisionCheckInterval = TimeSpan.FromMilliseconds(500);

    private long _lastCheckTicks;

    private void Refresh()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_loadedRevision != long.MinValue
                && now - _lastCheckTicks < (long)RevisionCheckInterval.TotalMilliseconds)
                return;
            _lastCheckTicks = now;
        }

        long revision;
        try
        {
            revision = _revision();
        }
        catch (Exception)
        {
            return;
        }

        lock (_gate)
        {
            if (revision >= 0 && revision == _loadedRevision)
                return;
        }

        IReadOnlyList<CommandInfo>? loaded;
        try
        {
            loaded = _load();
        }
        catch (Exception)
        {
            return;
        }

        if (loaded == null)
            return;
        var ordered = loaded.OrderBy(command => command.Name, StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            _commands = ordered;
            _byName = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in ordered)
                _byName.TryAdd(command.Name, command);
            _loadedRevision = revision;
        }
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
