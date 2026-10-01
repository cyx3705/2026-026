using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// Aurora 自己那批指令的登记表（1.29.0，宿主 6.0.0 统一契约）。
/// </summary>
/// <remarks>
/// <para>
/// 它不是第二套总线：这里只收描述符，不解析、不绑定、不执行。进程内装载时
/// <c>AuroraShellHost</c> 把整张表经 <c>IModuleContext.RegisterCommands</c> 原样登记进宿主，
/// 执行一律走宿主总线（Aurora 的指令都声明 <see cref="CommandDescriptor.RequiresUiThread"/>，由宿主编组到界面线程）。
/// </para>
/// <para>
/// 此前界面自建宿主的 <c>CommandRegistry</c> + <c>CommandBus</c>，把自己的指令逐字段抄成代理再登记进宿主，
/// 宿主一改总线实现就等于改 Aurora。
/// </para>
/// </remarks>
public sealed class CommandTable : ICommandRegistrar
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;

    /// <summary>登记或撤销后触发。</summary>
    public event Action? Changed;

    /// <summary>每次登记或撤销加一。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>登记一条；重名即抛（与宿主一致，禁止静默覆盖）。</summary>
    public void Register(CommandDescriptor descriptor) => Register(descriptor, "framework");

    /// <summary>登记一条并记下来源标签（只在本表内用，宿主按模块盖章）。</summary>
    public void Register(CommandDescriptor descriptor, string source)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ConfirmPrompt != null && descriptor.Level != CommandLevel.Ask)
        {
            throw new ArgumentException(
                $"指令 {descriptor.Name} 写了 ConfirmPrompt 但级别不是 {nameof(CommandLevel.Ask)}",
                nameof(descriptor));
        }

        lock (_gate)
        {
            if (!_commands.TryAdd(descriptor.Name, descriptor))
                throw new InvalidOperationException($"指令名冲突: {descriptor.Name} 已注册,禁止覆盖");
            _sources[descriptor.Name] = string.IsNullOrWhiteSpace(source) ? FrontendCommandSource.Name : source.Trim();
            _revision++;
        }

        Changed?.Invoke();
    }

    /// <summary>撤掉一条；存在则返回 true。</summary>
    public bool Unregister(string name)
    {
        bool removed;
        lock (_gate)
        {
            removed = _commands.Remove(name);
            _sources.Remove(name);
            if (removed)
                _revision++;
        }

        if (removed)
            Changed?.Invoke();
        return removed;
    }

    public bool TryGet(string name, out CommandDescriptor descriptor)
    {
        lock (_gate)
            return _commands.TryGetValue(name, out descriptor!);
    }

    /// <summary>全部指令，按名称排序。</summary>
    public IReadOnlyList<CommandDescriptor> All()
    {
        lock (_gate)
            return _commands.Values.OrderBy(command => command.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>登记时给的来源标签；未登记时为 <c>framework</c>。</summary>
    public string GetSource(string name)
    {
        lock (_gate)
            return _sources.GetValueOrDefault(name, "framework");
    }
}
