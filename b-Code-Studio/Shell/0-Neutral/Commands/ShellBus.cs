using System.Globalization;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// 界面各处用的总线（1.29.0，宿主 6.0.0 统一契约）：宿主那条窄总线加一份只读目录。
/// </summary>
/// <remarks>
/// <para>
/// 执行一律交给宿主（<see cref="ICommandBus"/>）：没有本地注册表、没有「发给宿主还是就地执行」的路由。
/// 此前界面自建宿主的 <c>CommandBus</c>，靠 <c>RemoteExecutor</c> / <c>ShouldUseRemoteCommand</c> 在两条总线间分流。
/// </para>
/// <para>
/// <see cref="Validate"/> 在界面侧按目录做（输入框每敲一个字都要问，不值得一次总线往返）；
/// 执行时宿主还会按同一规则再校验一遍，这里只负责即时提示。
/// </para>
/// </remarks>
public sealed class ShellBus(ICommandBus host, ShellCatalog catalog, CommandTable? pending = null)
{
    /// <summary>宿主的总线。</summary>
    public ICommandBus Host { get; } = host;

    /// <summary>只读目录。沿用旧名 Registry，界面各处的查法不变。</summary>
    public ShellCatalog Registry { get; } = catalog;

    /// <summary>
    /// 界面自己那张还没登记进宿主的指令表。进程内装载时 Aurora 的指令要等窗口建好才登记进宿主，
    /// 建窗期间（菜单、动作台账校验）宿主目录里还没有它们，校验按这张表认。登记之后宿主目录优先。
    /// </summary>
    public CommandTable? Pending { get; } = pending;

    /// <summary>经本对象执行的指令完成后触发（文本、来源、结果），在执行线程上引发。</summary>
    public event Action<string, string, CommandResult>? Executed;

    public async Task<CommandResult> ExecuteAsync(string text, string source, CancellationToken cancellation = default)
    {
        var result = await Host.ExecuteAsync(text, source, cancellation).ConfigureAwait(false);
        Executed?.Invoke(text, source, result);
        return result;
    }

    /// <summary>安静执行：取数用，不回显、不进控制台。</summary>
    public Task<CommandResult> InvokeAsync(string text, string source, CancellationToken cancellation = default)
        => Host.InvokeAsync(text, source, cancellation);

    /// <summary>只校验语法、指令是否存在与参数能否绑定；通过时为 null。</summary>
    public string? Validate(string text)
    {
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text.Trim());
        }
        catch (CommandSyntaxException ex)
        {
            return ex.Message;
        }

        if (Registry.TryGet(parsed.Name, out var command))
            return Bind(command, parsed);
        if (Pending != null && Pending.TryGet(parsed.Name, out var own))
            return Bind(CommandInfo.FromDescriptor(own, Pending.GetSource(own.Name)), parsed);
        return $"未知指令: {parsed.Name}";
    }

    /// <summary>用法行，如 <c>用法: aurora.ui.show name= [pos=left/right]</c>。</summary>
    public static string FormatUsage(CommandInfo command)
    {
        var parts = command.Parameters.Select(parameter =>
        {
            var core = parameter.AllowedValues is { Length: > 0 }
                ? $"{parameter.Name}={string.Join("/", parameter.AllowedValues)}"
                : $"{parameter.Name}=";
            return parameter.Required ? core : $"[{core}]";
        });
        return $"用法: {command.Name} {string.Join(" ", parts)}".TrimEnd();
    }

    /// <summary>参数绑定的界面侧检查（规则见宿主模块API「参数」一节）。</summary>
    private static string? Bind(CommandInfo command, ParsedCommand parsed)
    {
        if (command.AllowUnspecifiedParameters)
            return null;

        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = command.Parameters.Where(p => p.Position.HasValue).OrderBy(p => p.Position!.Value).ToArray();
        if (parsed.Positionals.Count > positional.Length)
            return $"多余的位置参数: {string.Join(" ", parsed.Positionals.Skip(positional.Length))}";
        for (var i = 0; i < parsed.Positionals.Count; i++)
            bound[positional[i].Name] = parsed.Positionals[i];

        foreach (var (key, value) in parsed.Named)
        {
            var spec = command.Parameters.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (spec == null)
            {
                var known = string.Join(" ", command.Parameters.Select(p => p.Name + "="));
                return $"未知参数: {key}=" + (known.Length > 0 ? $"(可用: {known})" : "(该指令不接受参数)");
            }

            bound[spec.Name] = value;
        }

        foreach (var spec in command.Parameters)
        {
            if (!bound.TryGetValue(spec.Name, out var value))
            {
                if (spec.Required)
                    return $"缺少必填参数: {spec.Name}=";
                continue;
            }

            var typeError = spec.Type switch
            {
                ParamType.Int when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为整数,实际: {value}",
                ParamType.Double when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为数值,实际: {value}",
                ParamType.Bool when value.ToLowerInvariant() is not ("true" or "false" or "1" or "0" or "yes" or "no" or "on" or "off")
                    => $"参数 {spec.Name} 应为 true/false,实际: {value}",
                _ => null,
            };
            if (typeError != null)
                return typeError;
            if (spec.AllowedValues is { Length: > 0 } && !spec.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                return $"参数 {spec.Name} 取值应为 {string.Join("/", spec.AllowedValues)},实际: {value}";
        }

        return null;
    }
}
