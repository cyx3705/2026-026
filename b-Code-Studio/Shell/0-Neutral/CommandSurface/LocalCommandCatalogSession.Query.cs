using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Neutral.Commands;

namespace HistoryAurora.Shell.Neutral.CommandSurface;

/// <summary>取数、筛选与选中。渲染侧只读这里的结果，不自己再过滤一遍。</summary>
internal sealed partial class LocalCommandCatalogSession
{
    /// <summary>按当前筛选可见的行。命令集页据此渲染，与控制台的域/类筛选是同一份口径。</summary>
    public IReadOnlyList<CatalogEntry> Visible()
    {
        IEnumerable<CatalogEntry> rows = _entries;

        if (_filter.Domain != All)
            rows = rows.Where(entry => entry.Domain.Equals(_filter.Domain, StringComparison.OrdinalIgnoreCase));

        if (_filter.CommandClass != All)
        {
            var key = CommandClassLabels.ToKey(_filter.CommandClass);
            rows = rows.Where(entry =>
                CommandClassLabels.ToKey(entry.CommandClass).Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(_filter.Query))
        {
            var query = _filter.Query.Trim();
            rows = rows.Where(entry =>
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Summary.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return rows.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
    }

    public CatalogEntry? Find(string? name)
        => _entries.FirstOrDefault(
            entry => entry.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>重建快照：读目录（宿主目录已含 Aurora 自己的指令）。</summary>
    public Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        => _refresh.RunAsync(() => RefreshCoreAsync(force));

    private Task<bool> RefreshCoreAsync(bool force)
    {
        var next = LocalEntries().OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();
        var changed = force || !SameNames(next, _entries);
        _entries = next;
        _parameters.Clear();
        NormalizeFilter();

        if (changed)
            Raise(CommandCatalogChangeKind.Snapshot);
        return Task.FromResult(true);
    }

    public void SetFilter(CommandCatalogFilter filter)
    {
        _filter = filter ?? new CommandCatalogFilter();
        NormalizeFilter();
        Raise(CommandCatalogChangeKind.Filter);
    }

    public bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains)
    {
        availableDomains = [All, .. Domains];
        if (!availableDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return false;

        _filter = _filter with { Domain = Canonical(availableDomains, domain) };
        NormalizeFilter();
        Raise(CommandCatalogChangeKind.Filter);
        return true;
    }

    public bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses)
    {
        availableClasses = [All, .. Classes];
        if (!availableClasses.Contains(commandClass, StringComparer.OrdinalIgnoreCase))
            return false;

        _filter = _filter with { CommandClass = Canonical(availableClasses, commandClass) };
        Raise(CommandCatalogChangeKind.Filter);
        return true;
    }

    public void SetConsoleQuery(string query)
    {
        var normalized = query ?? "";
        if (string.Equals(_consoleQuery, normalized, StringComparison.Ordinal))
            return;
        _consoleQuery = normalized;
        ConsoleQueryChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool MoveSelection(int direction)
    {
        var visible = Visible();
        if (visible.Count == 0 || direction == 0)
            return false;

        var index = _selected == null
            ? -1
            : IndexOf(visible, _selected);
        var next = Math.Clamp(index + Math.Sign(direction), 0, visible.Count - 1);
        Select(visible[next].Name);
        return true;
    }

    public void Select(string? commandName)
    {
        var normalized = string.IsNullOrWhiteSpace(commandName) ? null : commandName.Trim();
        if (string.Equals(_selected, normalized, StringComparison.OrdinalIgnoreCase))
            return;
        _selected = normalized;
        Raise(CommandCatalogChangeKind.Selection);
    }

    private static int IndexOf(IReadOnlyList<CatalogEntry> rows, string name)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private List<CatalogEntry> LocalEntries() => _registry.All()
        .Select(command => new CatalogEntry(
            command.Name,
            command.Domain,
            command.CommandClass,
            command.Summary,
            command.Example,
            command.Readonly,
            command.Level == CommandLevel.Ask,
            command.Source,
            command.HiddenReason))
        .ToList();

    private static bool SameNames(List<CatalogEntry> left, List<CatalogEntry> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Name.Equals(right[i].Name, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>筛选值随快照收敛：域没了就退回「全部」，否则页面会一直显示 0 行而看不出原因。</summary>
    private void NormalizeFilter()
    {
        if (_filter.Domain != All && !Domains.Contains(_filter.Domain, StringComparer.OrdinalIgnoreCase))
            _filter = _filter with { Domain = All };
        if (_filter.Domain == All && _filter.CommandClass != All)
            _filter = _filter with { CommandClass = All };
        if (_filter.CommandClass != All
            && !Classes.Contains(_filter.CommandClass, StringComparer.OrdinalIgnoreCase))
            _filter = _filter with { CommandClass = All };
    }

    private static string Canonical(IReadOnlyList<string> choices, string value)
        => choices.FirstOrDefault(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value;

    private void OnRegistryChanged() => Raise(CommandCatalogChangeKind.Invalidated);

    private void Raise(CommandCatalogChangeKind kind)
    {
        if (_disposed)
            return;
        Changed?.Invoke(this, new CommandCatalogChangedEventArgs(kind));
    }
}
