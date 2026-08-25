using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Commands;

namespace HistoryAurora.Shell.CommandSurface;

/// <summary>目录里的一行：一条指令的注册事实，本地与宿主两侧归一成同一个形状。</summary>
internal sealed record CatalogEntry(
    string Name,
    string Domain,
    string CommandClass,
    string Summary,
    string? Example,
    bool Readonly,
    bool Dangerous,
    string Source,
    string? HiddenReason);

/// <summary>
/// 命令目录会话的 Aurora 自建实现（REQ-UI-014）。
///
/// 为什么必须自建：这套会话原先由 HistoryMercury 的命令工作台提供，
/// 宿主 5.0 拆掉界面 SDK 之后挂载点消失，命令集与指令详情两页**整体不见了**，
/// 控制台的 Tab 补全也随之变成死路（<c>aurora.ui.show name=mcp</c> 报窗口不存在）。
/// 而"这条指令收什么参数"是控制台以外唯一能查的地方——它不能挂在某个模块在不在场上。
///
/// 数据来源是两处的并集：
/// <list type="bullet">
///   <item>Aurora 自己的注册表（<c>aurora.*</c>，界面内自持）；</item>
///   <item>接了远端执行器时，宿主的 <c>vulcan.command.list</c>（模块与宿主的全部指令）。</item>
/// </list>
/// 同名以本地为准：本地那条才是真正会被执行到的。
/// </summary>
internal sealed partial class LocalCommandCatalogSession : ICommandCatalogSession
{
    private const string All = "全部";

    private readonly CommandRegistry _registry;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly Dictionary<string, IReadOnlyList<CommandParameterInfo>> _parameters =
        new(StringComparer.OrdinalIgnoreCase);

    private List<CatalogEntry> _entries;
    private CommandCatalogFilter _filter = new();
    private string? _selected;
    private string _consoleQuery = "";
    private bool _disposed;

    public LocalCommandCatalogSession(CommandBus bus, IShellLog log)
    {
        _bus = bus;
        _log = log;
        _registry = bus.Registry;
        _entries = LocalEntries();

        // 注册表随模块热重载而变。这里只发"作废"，不在事件线程上取数——
        // Changed 可能来自任意线程，而订阅方（控制台、命令集页）各自回自己的调度器。
        _registry.Changed += OnRegistryChanged;
    }

    public event EventHandler<CommandCatalogChangedEventArgs>? Changed;

    /// <summary>
    /// 控制台输入框的内容变了。**不走 <see cref="Changed"/>**：控制台正是 Changed 的订阅方，
    /// 而它在处理 Changed 时又会回头设置这个查询串——两者接在一起就是一个自激回路。
    /// </summary>
    public event EventHandler? ConsoleQueryChanged;

    public IReadOnlyList<CatalogEntry> Entries => _entries;

    public string ConsoleQuery => _consoleQuery;

    public IReadOnlyList<string> Domains => _entries
        .Select(entry => entry.Domain)
        .Where(domain => !string.IsNullOrWhiteSpace(domain))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(domain => domain, StringComparer.Ordinal)
        .ToList();

    /// <summary>严格两级：域为「全部」时不列类，否则类的取值范围随域收敛（DEC-021）。</summary>
    public IReadOnlyList<string> Classes
    {
        get
        {
            if (_filter.Domain == All)
                return [];
            return _entries
                .Where(entry => entry.Domain.Equals(_filter.Domain, StringComparison.OrdinalIgnoreCase))
                .Select(entry => CommandClassLabels.Display(entry.CommandClass))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value == CommandClassLabels.None ? 1 : 0)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToList();
        }
    }

    public string? SelectedCommandName => _selected;

    public CommandCatalogFilter CurrentFilter => _filter;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _registry.Changed -= OnRegistryChanged;
    }
}
