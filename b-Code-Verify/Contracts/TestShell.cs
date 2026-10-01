// 宿主 6.0.0 统一契约下 Aurora 契约测试的宿主替身（1.29.0）。
//
// 生产里界面的总线是宿主那条：Aurora 的指令登记进宿主，执行、确认、界面线程编组都在宿主。
// 测试进程里没有宿主，这里按宿主总线的公开规则写一份最小实现，直接执行本仓 CommandTable 里的指令：
// 解析、参数绑定（位置 / 键=值 / 必填 / 类型 / 取值）、级别为 Ask 时问确认、处理器异常转失败。
// 真实装载与执行以 HistoryVulcan.Cli.exe --probe 为准（ModuleSmoke）。

using System.Collections.Concurrent;
using System.Globalization;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Verify;

internal static class TestShell
{
    /// <summary>以 <paramref name="table"/> 为全部指令的界面总线：目录与执行都来自这张表。</summary>
    public static ShellBus Bus(CommandTable table, Func<string, bool>? confirm = null)
        => new(new TableBus(table, confirm), ShellCatalog.FromTable(table));

    /// <summary>同上，另像宿主一样把回显与结果写进 <paramref name="log"/>（控制台就是从这里显示的）。</summary>
    public static ShellBus Bus(CommandTable table, IModuleLog log)
        => new(new TableBus(table) { Log = log }, ShellCatalog.FromTable(table));

    /// <summary>
    /// 表里没有的指令（多是宿主的 <c>vulcan.*</c>）交给 <paramref name="host"/> 应答；目录仍来自表。
    /// </summary>
    public static ShellBus Bus(CommandTable table, Func<string, string, Task<CommandResult>> host)
        => new(new TableBus(table) { Fallback = host }, ShellCatalog.FromTable(table));

    /// <summary>给 ShellWindow 用：一张空的自有指令表和以它为全部指令的界面总线。</summary>
    public static ShellBus NewBus(IModuleLog log, out CommandTable table)
    {
        table = new CommandTable();
        return Bus(table, log);
    }

    /// <summary>
    /// 照进程内装载的次序：宿主目录（<paramref name="hostTable"/>）里还没有界面自己的指令，
    /// 它们只在 <paramref name="table"/> 里，等窗口建好才登记进宿主。
    /// </summary>
    public static ShellBus InProcessBus(IModuleLog log, CommandTable hostTable, out CommandTable table)
    {
        table = new CommandTable();
        return new(new TableBus(hostTable) { Log = log }, ShellCatalog.FromTable(hostTable), table);
    }

    /// <summary>取回替身总线，看它收到过哪些调用。</summary>
    public static TableBus Calls(ShellBus bus) => (TableBus)bus.Host;
}

/// <summary>执行一张 <see cref="CommandTable"/> 里的指令；其余指令交给 <see cref="Fallback"/>。</summary>
internal sealed class TableBus(CommandTable table, Func<string, bool>? confirm = null) : ICommandBus
{
    private readonly ConcurrentQueue<(string Text, string Source)> _calls = new();

    public CommandTable Table { get; } = table;

    public IReadOnlyList<(string Text, string Source)> Calls => _calls.ToArray();

    public Func<string, string, Task<CommandResult>>? Fallback { get; set; }

    public IProgress<string>? Progress { get; set; }

    /// <summary>设了就像宿主那样写回显 <c>cmd:&lt;来源&gt;</c> 与结果 <c>cmd:result:&lt;域&gt;:&lt;类&gt;</c>。</summary>
    public IModuleLog? Log { get; set; }

    public async Task<CommandResult> ExecuteAsync(string text, string source, CancellationToken cancellation = default)
    {
        Log?.Log(ShellLogLevel.Info, "cmd:" + source, EchoText(text));
        var result = await RunAsync(text, source, cancellation).ConfigureAwait(true);
        if (Log != null)
        {
            var name = text.Trim().Split(' ', 2)[0];
            var info = Table.TryGet(name, out var descriptor) ? CommandInfo.FromDescriptor(descriptor, Table.GetSource(name)) : null;
            Log.Log(
                result.Success ? ShellLogLevel.Info : ShellLogLevel.Error,
                $"cmd:result:{info?.Domain ?? CommandNames.LegacyDomain(name)}:{info?.CommandClass ?? CommandNames.LegacyClass(name)}",
                (result.Success ? "✓ " : "✗ ") + result.Message);
        }

        return result;
    }

    public Task<CommandResult> InvokeAsync(string text, string source, CancellationToken cancellation = default)
        => RunAsync(text, source, cancellation);

    public bool RequestConfirmation(string prompt) => confirm?.Invoke(prompt) ?? false;

    private async Task<CommandResult> RunAsync(string text, string source, CancellationToken cancellation)
    {
        _calls.Enqueue((text, source));
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text.Trim());
        }
        catch (CommandSyntaxException ex)
        {
            return CommandResult.Fail(ex.Message);
        }

        if (!Table.TryGet(parsed.Name, out var descriptor))
        {
            return Fallback != null
                ? await Fallback(text, source).ConfigureAwait(false)
                : CommandResult.Fail($"未知指令: {parsed.Name}");
        }

        var error = Bind(descriptor, parsed, out var values);
        if (error != null)
            return CommandResult.Fail($"{error}\n{ShellBus.FormatUsage(CommandInfo.FromDescriptor(descriptor, "test"))}");

        var context = new CommandContext(descriptor, values, source, Progress, cancellation);
        if (descriptor.Level == CommandLevel.Ask)
        {
            var prompt = descriptor.ConfirmPrompt == null ? $"确认执行 {descriptor.Name}？" : descriptor.ConfirmPrompt(context);
            if (prompt != null && confirm == null)
                return CommandResult.Fail("该指令需要二次确认,但当前环境没有确认通道,已拒绝执行");
            if (prompt != null && !confirm!(prompt))
                return CommandResult.Fail("已取消(用户未确认)");
        }

        try
        {
            return await descriptor.Handler(context).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"{descriptor.Name} 执行异常: {ex.GetType().Name}");
        }
    }

    /// <summary>宿主回显的脱敏写法（宿主模块开发手册「命令契约」一节的脱敏规则）：敏感名的参数、敏感键的 app.set 值、语法错误整段。</summary>
    private string EchoText(string text)
    {
        const string redacted = "[REDACTED]";
        var trimmed = text.Trim();
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(trimmed);
        }
        catch (CommandSyntaxException)
        {
            var separator = trimmed.IndexOfAny([' ', (char)9, (char)13, (char)10]);
            return separator < 0 ? trimmed : trimmed[..separator] + " " + redacted;
        }

        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        var secretSetting = parsed.Name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase)
                            && key != null && IsSensitive(key);
        var positions = new HashSet<int>();
        if (secretSetting)
            positions.Add(1);
        if (IsSensitive(parsed.Name))
            positions.Add(0);
        if (Table.TryGet(parsed.Name, out var descriptor))
        {
            var ordered = descriptor.Parameters.Where(p => p.Position.HasValue).OrderBy(p => p.Position!.Value).ToList();
            for (var i = 0; i < ordered.Count; i++)
                if (IsSensitive(ordered[i].Name))
                    positions.Add(i);
        }

        bool SensitiveParameter(string name)
            => IsSensitive(name) || (secretSetting && name.Equals("value", StringComparison.OrdinalIgnoreCase));

        var parts = new List<string> { parsed.Name };
        parts.AddRange(parsed.Positionals.Select((value, index) => CommandParser.QuoteArg(positions.Contains(index) ? redacted : value)));
        parts.AddRange(parsed.Named.Select(pair => $"{pair.Key}={CommandParser.QuoteArg(SensitiveParameter(pair.Key) ? redacted : pair.Value)}"));
        return string.Join(' ', parts);
    }

    private static bool IsSensitive(string name)
    {
        var normalized = name.Replace(".", "").Replace("_", "").Replace("-", "");
        return normalized.Equals("code", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("passwd", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Bind(CommandDescriptor descriptor, ParsedCommand parsed, out IReadOnlyDictionary<string, string> values)
    {
        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values = bound;
        if (descriptor.AllowUnspecifiedParameters)
        {
            foreach (var (key, value) in parsed.Named)
                bound[key] = value;
            return null;
        }

        var positional = descriptor.Parameters.Where(p => p.Position.HasValue).OrderBy(p => p.Position!.Value).ToArray();
        if (parsed.Positionals.Count > positional.Length)
            return $"多余的位置参数: {string.Join(" ", parsed.Positionals.Skip(positional.Length))}";
        for (var i = 0; i < parsed.Positionals.Count; i++)
            bound[positional[i].Name] = parsed.Positionals[i];

        foreach (var (key, value) in parsed.Named)
        {
            var spec = descriptor.Parameters.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (spec == null)
            {
                var known = string.Join(" ", descriptor.Parameters.Select(p => p.Name + "="));
                return $"未知参数: {key}=" + (known.Length > 0 ? $"(可用: {known})" : "(该指令不接受参数)");
            }

            bound[spec.Name] = value;
        }

        foreach (var spec in descriptor.Parameters)
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
