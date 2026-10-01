using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>
/// 目录里的一条指令（1.29.0）：按宿主模块API写明的 <c>vulcan.command.list</c> 行形状读出。
/// </summary>
/// <remarks>
/// 成员名与 <see cref="CommandDescriptor"/> 的只读面对齐（Parameters / Level / Readonly / Annotation …），
/// 界面代码从「查宿主注册表拿描述符」换到「查目录拿这一条」时写法不变。它没有处理器：要执行就经总线。
/// </remarks>
public sealed class CommandInfo
{
    public required string Name { get; init; }

    public required string Domain { get; init; }

    /// <summary>域内功能类；两段名为空串（无类）。</summary>
    public required string CommandClass { get; init; }

    public string Method => CommandNames.MethodOf(Name);

    public required string Summary { get; init; }

    public string? Example { get; init; }

    /// <summary>来源：<c>framework:service</c>、<c>module:&lt;模块名&gt;</c>、<c>framework:frontend</c> 等。</summary>
    public required string Source { get; init; }

    public CommandLevel Level { get; init; }

    public bool Readonly { get; init; }

    public string? HiddenReason { get; init; }

    public bool RequiresUiThread { get; init; }

    public bool RequiresConfirmation { get; init; }

    public bool AllowUnspecifiedParameters { get; init; }

    public IReadOnlyList<ParameterSpec> Parameters { get; init; } = [];

    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Annotation(string key) => Annotations.TryGetValue(key, out var value) ? value : null;

    /// <summary>按 C7 形状读一行；没有名字时返回 null。</summary>
    public static CommandInfo? FromJson(JsonElement row)
    {
        var name = Text(row, "commandName");
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var source = Text(row, "source") ?? "framework";
        var detail = Text(row, "sourceDetail");
        var parameters = new List<ParameterSpec>();
        if (row.TryGetProperty("parameters", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var parameterName = Text(item, "name");
                if (string.IsNullOrWhiteSpace(parameterName))
                    continue;
                var allowed = item.TryGetProperty("allowedValues", out var values) && values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                    : [];
                parameters.Add(new ParameterSpec
                {
                    Name = parameterName,
                    Description = Text(item, "description") ?? "",
                    Type = (Text(item, "type") ?? "string").ToLowerInvariant() switch
                    {
                        "int" => ParamType.Int,
                        "double" => ParamType.Double,
                        "bool" => ParamType.Bool,
                        _ => ParamType.String,
                    },
                    Required = Flag(item, "required"),
                    Default = Text(item, "default"),
                    Position = item.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Number
                        ? position.GetInt32()
                        : null,
                    AllowedValues = allowed.Length == 0 ? null : allowed,
                });
            }
        }

        var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (row.TryGetProperty("annotations", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in map.EnumerateObject())
            {
                if (pair.Value.ValueKind == JsonValueKind.String)
                    annotations[pair.Name] = pair.Value.GetString() ?? "";
            }
        }

        return new CommandInfo
        {
            Name = name,
            Domain = Text(row, "domain") ?? CommandNames.LegacyDomain(name),
            CommandClass = Text(row, "commandClass") ?? CommandNames.LegacyClass(name),
            Summary = Text(row, "summary") ?? "",
            Example = Text(row, "example"),
            Source = detail is { Length: > 0 } ? $"{source}:{detail}" : source,
            Level = Flag(row, "dangerous") ? CommandLevel.Ask : CommandLevel.Run,
            Readonly = Flag(row, "readonly"),
            HiddenReason = Text(row, "hiddenReason"),
            RequiresUiThread = Flag(row, "requiresUiThread"),
            RequiresConfirmation = Flag(row, "requiresConfirmation"),
            AllowUnspecifiedParameters = Flag(row, "allowUnspecifiedParameters"),
            Parameters = parameters,
            Annotations = annotations,
        };
    }

    /// <summary>
    /// 从本仓自己登记的描述符投影（宿主不在时的独立运行与测试用）。域与类的推导与宿主模块API写明的规则一致。
    /// </summary>
    public static CommandInfo FromDescriptor(CommandDescriptor descriptor, string source)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var domain = source.StartsWith("module:", StringComparison.OrdinalIgnoreCase)
                     && source["module:".Length..].Trim() is { Length: > 0 } owner
            ? ModuleDomainNaming.ToDomain(owner)
            : string.IsNullOrWhiteSpace(descriptor.Domain)
                ? CommandNames.LegacyDomain(descriptor.Name)
                : descriptor.Domain.Trim();
        return new CommandInfo
        {
            Name = descriptor.Name,
            Domain = domain,
            CommandClass = string.IsNullOrWhiteSpace(descriptor.CommandClass)
                ? CommandNames.LegacyClass(descriptor.Name)
                : descriptor.CommandClass.Trim().ToLowerInvariant(),
            Summary = descriptor.Summary,
            Example = descriptor.Example,
            Source = source,
            Level = descriptor.Level,
            Readonly = descriptor.Readonly,
            HiddenReason = string.IsNullOrEmpty(descriptor.HiddenReason) ? null : descriptor.HiddenReason,
            RequiresUiThread = descriptor.RequiresUiThread,
            RequiresConfirmation = descriptor.ConfirmPrompt != null,
            AllowUnspecifiedParameters = descriptor.AllowUnspecifiedParameters,
            Parameters = descriptor.Parameters,
            Annotations = descriptor.Annotations,
        };
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>指令名的结构推导（宿主模块API「指令命名」一节的规则，纯函数）。</summary>
public static class CommandNames
{
    /// <summary>首段；无点则 <c>core</c>。</summary>
    public static string LegacyDomain(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : "core";
    }

    /// <summary>三段及以上取第二段；两段是无类直接方法（空串）；单段 <c>core</c>。</summary>
    public static string LegacyClass(string name)
    {
        var parts = name.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
            return parts[1].ToLowerInvariant();
        return parts.Length == 2 ? string.Empty : "core";
    }

    /// <summary>末段；无点则整名。</summary>
    public static string MethodOf(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        return (dot >= 0 ? trimmed[(dot + 1)..] : trimmed).ToLowerInvariant();
    }
}
