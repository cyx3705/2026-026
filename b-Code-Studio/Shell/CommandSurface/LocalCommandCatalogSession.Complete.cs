using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Commands;

namespace HistoryAurora.Shell.CommandSurface;

/// <summary>
/// 分段补全（REQ-UI-013）。三段之间的边界就是指令名本身的结构：
/// <c>&lt;域&gt;.&lt;类&gt;.&lt;方法&gt; 参数=值</c>。
///
/// 光标落在第一段（指令名）上时补指令名；落在其后时补参数名，
/// 已经写了 <c>=</c> 就补该参数的允许值。参数表本地拿不到时向宿主问一次
/// <c>vulcan.command.show</c> 并缓存——每敲一个键都去问一遍宿主是不必要的，
/// 而参数表在一次会话里不会变。
/// </summary>
internal sealed partial class LocalCommandCatalogSession
{
    public async Task<ConsoleCompletionResult> CompleteAsync(
        string text,
        int caretIndex,
        CancellationToken cancellationToken = default)
    {
        var source = text ?? "";
        var caret = Math.Clamp(caretIndex, 0, source.Length);
        var start = caret == 0 ? 0 : source.LastIndexOf(' ', caret - 1) + 1;
        var end = source.IndexOf(' ', caret);
        if (end < 0)
            end = source.Length;
        var token = source[start..end];

        if (start == 0)
            return CompleteCommandName(token, start, end - start);

        var commandName = source.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parameters = await ParametersAsync(commandName, cancellationToken).ConfigureAwait(true);
        if (parameters.Count == 0)
            return ConsoleCompletionResult.Empty;

        var separator = token.IndexOf('=');
        if (separator < 0)
            return CompleteParameterName(source, token, parameters, start, end - start);

        var name = token[..separator];
        var prefix = token[(separator + 1)..];
        var parameter = parameters.FirstOrDefault(
            candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (parameter == null || parameter.AllowedValues.Count == 0)
            return ConsoleCompletionResult.Empty;

        var values = parameter.AllowedValues
            .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(value => new ConsoleCompletionCandidate
            {
                InsertText = value,
                DisplayText = value,
                Description = parameter.Description,
                Kind = ConsoleCompletionKind.Value,
            })
            .ToList();

        return values.Count == 0
            ? ConsoleCompletionResult.Empty
            : new ConsoleCompletionResult
            {
                Candidates = values,
                ReplaceStart = start + separator + 1,
                ReplaceLength = prefix.Length,
            };
    }

    /// <summary>
    /// 指令名补全。前缀匹配优先；一条都不匹配时退回包含匹配——
    /// 只记得中间一段（"想不起来是 dock 还是 layout"）是这里最常见的用法。
    /// </summary>
    private ConsoleCompletionResult CompleteCommandName(string token, int start, int length)
    {
        if (token.Length == 0)
            return ConsoleCompletionResult.Empty;

        var matched = _entries
            .Where(entry => entry.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matched.Count == 0)
        {
            matched = _entries
                .Where(entry => entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matched.Count == 0)
            return ConsoleCompletionResult.Empty;

        var candidates = matched
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(50)
            .Select(entry => new ConsoleCompletionCandidate
            {
                InsertText = entry.Name,
                DisplayText = entry.Name,
                Description = entry.Summary,
                Kind = ConsoleCompletionKind.Command,
            })
            .ToList();

        return new ConsoleCompletionResult
        {
            Candidates = candidates,
            ReplaceStart = start,
            ReplaceLength = length,
        };
    }

    /// <summary>参数名补全。已经写过的参数不再列出——重复给同一个参数赋值是语法错。</summary>
    private static ConsoleCompletionResult CompleteParameterName(
        string source,
        string token,
        IReadOnlyList<CommandParameterInfo> parameters,
        int start,
        int length)
    {
        var used = source
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Where(part => part.Contains('='))
            .Select(part => part[..part.IndexOf('=')])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = parameters
            .Where(parameter => !used.Contains(parameter.Name))
            .Where(parameter => parameter.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Select(parameter => new ConsoleCompletionCandidate
            {
                InsertText = parameter.Name + "=",
                DisplayText = parameter.Name + (parameter.Required ? "=（必填）" : "="),
                Description = parameter.Description,
                Kind = ConsoleCompletionKind.Parameter,
            })
            .ToList();

        return candidates.Count == 0
            ? ConsoleCompletionResult.Empty
            : new ConsoleCompletionResult
            {
                Candidates = candidates,
                ReplaceStart = start,
                ReplaceLength = length,
            };
    }
}
