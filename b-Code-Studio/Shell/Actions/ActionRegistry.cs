using System.Text.RegularExpressions;
using HistoryAurora.Shell.Modules;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Actions;

/// <summary>一次动作声明拉取的结果。</summary>
public sealed record ActionLoadReport(
    int ModulesAsked,
    int ActionsDeclared,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Broken);

/// <summary>解析一次绑定的结果。失败一定带一句能直接显示给人看的原因。</summary>
public readonly record struct ActionBinding(ActionDeclaration? Action, string? Error)
{
    public bool Ok => Action != null;

    public static ActionBinding Fail(string reason) => new(null, reason);
}

/// <summary>
/// 动作声明台账（REQ-UI-009）。按钮绑的是这里的 id，不是指令名。
///
/// 与页面描述同为"拉取"模型：Aurora 主动问 <c>&lt;域&gt;.ui.actions</c>，
/// 宿主不持有声明。声明是派生状态，模块卸载后台账跟着空，不会留下幽灵动作。
/// </summary>
public sealed partial class ActionRegistry(CommandBus bus, IShellLog log)
{
    private const string Source = "action";

    /// <summary>协议约定的声明命令后缀；模块以自己的域注册，例如 janus.ui.actions。</summary>
    public const string ActionsSuffix = ".ui.actions";

    private readonly Dictionary<string, ActionDeclaration> _actions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Aurora 自己声明的动作。与模块拉来的那批分开存,原因有两条:
    ///
    /// 一是 <see cref="ReloadAsync"/> 每轮都 Clear 模块声明——本地声明跟着被清掉的话,
    /// 任何一次模块重载都会让界面自带页面上的按钮集体变成「未声明的动作」。
    /// 二是拉取协议按 <c>&lt;域&gt;.ui.actions</c> 反推 owner,而 Aurora 把自己排除在拉取之外
    /// (见 <see cref="ModuleCommandProbe.SelfDomain"/>);界面自带的页面要声明动作,
    /// 只能走这条明路,不能把自己伪装成一个模块域塞进拉取里。
    /// </summary>
    private readonly Dictionary<string, ActionDeclaration> _local =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _broken = [];

    /// <summary>当前全部已声明动作，按 id 排序。</summary>
    public IReadOnlyList<ActionDeclaration> Actions
        => _actions.Values
            .Concat(_local.Values.Where(a => !_actions.ContainsKey(a.Id)))
            .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 登记一批由 Aurora 自己实现的动作(界面自带页面用)。幂等,重复调用按覆盖处理。
    /// 模块声明优先:同 id 时以模块那份为准,本地这份只在模块没有声明时兜底。
    /// </summary>
    public void DeclareLocal(string owner, IEnumerable<ActionDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        foreach (var action in declarations)
        {
            action.Owner = owner;
            _local[action.Id] = action;
        }
    }

    /// <summary>
    /// 声明了、但其 <c>command</c> 在注册表里不存在的动作。
    /// 这正是"改指令导致按钮失效"的原形，只不过现在它有名有姓地记在账上，
    /// 而不是等用户点了按钮才发现。
    /// </summary>
    public IReadOnlyList<string> Broken => _broken.ToList();

    /// <summary>重新向全部模块拉取动作声明。</summary>
    public async Task<ActionLoadReport> ReloadAsync(CancellationToken cancellation = default)
    {
        _actions.Clear();
        _broken.Clear();

        var owners = await ModuleCommandProbe
            .OwnersWithSuffixAsync(bus, log, Source, ActionsSuffix, cancellation)
            .ConfigureAwait(true);

        var skipped = new List<string>();
        foreach (var domain in owners)
            await LoadOwnerAsync(domain, skipped, cancellation).ConfigureAwait(true);

        foreach (var action in _actions.Values)
        {
            if (!bus.Registry.TryGet(action.Command, out _) && bus.RemoteExecutor == null)
                _broken.Add($"{action.Id} → {action.Command}（注册表里没有这条指令）");
        }

        log.Log(ShellLogLevel.Info, Source,
            $"动作声明拉取完成: 问了 {owners.Count} 个模块，收下 {_actions.Count} 条"
            + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 个" : "")
            + (_broken.Count > 0 ? $"，{_broken.Count} 条断链" : ""));

        return new ActionLoadReport(owners.Count, _actions.Count, skipped, Broken);
    }

    /// <summary>
    /// 按 id 取动作。取不到时返回**为什么**取不到，调用方必须把它显示出来——
    /// 「按钮点了没反应」是本轮要消灭的形态，静默禁用只是把它换了个样子。
    /// </summary>
    public ActionBinding Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ActionBinding.Fail("按钮未声明 action");
        if (!_actions.TryGetValue(id, out var action) && !_local.TryGetValue(id, out action))
            return ActionBinding.Fail($"未声明的动作: {id}");
        return new ActionBinding(action, null);
    }

    /// <summary>
    /// 组装动作对应的指令文本。<paramref name="lookup"/> 按控件 id 取当前值，
    /// 返回 null 表示没有这个控件。占位符引用不存在的控件时整条拒绝执行，
    /// 不把 <c>{x}</c> 原样发上总线——那会变成一条参数明显错误、却成功执行的指令。
    /// </summary>
    public static string? BuildCommandText(
        ActionDeclaration action,
        Func<string, string?> lookup,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(lookup);

        error = "";
        var text = action.Command;
        var unknown = new List<string>();

        foreach (var pair in action.Args ?? new Dictionary<string, string>())
        {
            var value = PlaceholderPattern().Replace(pair.Value ?? "", match =>
            {
                var control = match.Groups[1].Value;
                var resolved = lookup(control);
                if (resolved != null)
                    return resolved;
                unknown.Add(control);
                return match.Value;
            });

            text += " " + pair.Key + "=" + CommandParser.QuoteArg(value);
        }

        if (unknown.Count == 0)
            return text;

        error = $"动作 {action.Id} 引用了不存在的控件 {{{string.Join("}, {", unknown.Distinct())}}}";
        return null;
    }

    private async Task LoadOwnerAsync(string domain, List<string> skipped, CancellationToken cancellation)
    {
        CommandResult result;
        try
        {
            result = await bus.ExecuteAsync(domain + ActionsSuffix, "UI", cancellation).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Skip(skipped, domain, "调用失败: " + ex.Message);
            return;
        }

        if (!result.Success)
        {
            Skip(skipped, domain, result.Message);
            return;
        }

        // 声明可能来自跨进程中继，结构化载荷不保证存活，因此同样接受 Message 承载。
        var payload = result.Data as string ?? result.Message;
        var parsed = ActionDeclarationReader.Read(payload, ModuleCommandProbe.ExpectedOwner(domain));
        if (!parsed.Ok)
        {
            Skip(skipped, domain, parsed.Error!);
            return;
        }

        foreach (var action in parsed.Value!.Actions)
        {
            if (_actions.TryGetValue(action.Id, out var existing))
            {
                // 撞 id 保留先到的那一条：后到的覆盖会让"按钮到底调了谁"取决于模块装载顺序。
                log.Log(ShellLogLevel.Warn, Source,
                    $"{parsed.Value.Owner} 声明的动作 {action.Id} 与 {existing.Owner} 重名，已忽略后者");
                continue;
            }

            _actions[action.Id] = action;
        }
    }

    private void Skip(List<string> skipped, string domain, string reason)
    {
        skipped.Add(domain);
        log.Log(ShellLogLevel.Warn, Source, $"跳过模块 {domain} 的动作声明: {reason}");
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();
}
