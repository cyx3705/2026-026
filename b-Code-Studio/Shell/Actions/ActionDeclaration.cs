using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryAurora.Shell.Actions;

/// <summary>
/// 动作声明协议 V1（REQ-UI-009）。模块以 <c>&lt;域&gt;.ui.actions</c> 返回本结构，
/// 声明"我这里有哪些可被按钮绑定的方法"。
///
/// **为什么按钮不能直接写指令名**：面板声明与页面描述是两份独立的东西，
/// 而指令名归模块所有。模块改一次指令名，所有写死该名字的按钮同时变成哑巴——
/// 点了没反应、没有报错、也没有任何地方记下这件事。
/// 改成"按钮绑动作 id、动作由模块自己声明"之后，改名只需模块改自己的声明，
/// 按钮那一侧一个字都不用动；反过来,如果模块把声明删了，
/// Aurora 立刻知道这个按钮没有落点，并在界面上直说，而不是装作还能用。
/// </summary>
public sealed class ActionDeclarationSet
{
    /// <summary>当前支持的唯一 schema 版本。文本协议没有编译器保护，版本必须显式比对。</summary>
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    /// <summary>提供方模块名，与宿主注册表里的 owner 一致。</summary>
    public string Owner { get; init; } = "";

    public IReadOnlyList<ActionDeclaration> Actions { get; init; } = [];
}

/// <summary>一个可被按钮绑定的方法。</summary>
public sealed class ActionDeclaration
{
    /// <summary>
    /// 动作标识。要求小写、无空格、体系内唯一；建议以自己的域起头（<c>janus.branch.rename</c>）。
    /// **这是按钮唯一记住的东西**，因此它比指令名更需要稳定。
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>按钮上的默认文字；声明侧可以覆盖。</summary>
    public string Title { get; init; } = "";

    /// <summary>真正执行的指令名。改名只改这里。</summary>
    public string Command { get; init; } = "";

    /// <summary>
    /// 固定参数与占位参数。值里的 <c>{控件id}</c> 由 Aurora 用同面板控件的当前值替换。
    /// 占位符引用了不存在的控件时按钮拒绝执行并报错，不静默把 <c>{x}</c> 当字面量发出去。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Args { get; init; }

    /// <summary>危险档：按钮用 danger 样式。是否再弹确认由指令自己经宿主 ConfirmPrompt 决定。</summary>
    public bool Danger { get; init; }

    /// <summary>一句话说明，作为按钮 ToolTip。</summary>
    public string? Summary { get; init; }

    /// <summary>声明方模块名，由 Aurora 在收下时填入，不由模块自报。</summary>
    [JsonIgnore]
    public string Owner { get; internal set; } = "";
}

/// <summary>解析结果：成功带声明集，失败带可直接写进日志的原因。</summary>
public readonly record struct ActionDeclarationParse(ActionDeclarationSet? Value, string? Error)
{
    public bool Ok => Value != null;

    public static ActionDeclarationParse Fail(string reason) => new(null, reason);
}

public static class ActionDeclarationReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 解析模块返回的动作声明。校验口径与页面描述协议一致：
    /// 版本不对、owner 冒名、id 不规范或重复，整份作废，不做部分接受。
    /// </summary>
    public static ActionDeclarationParse Read(string? json, string expectedOwner)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ActionDeclarationParse.Fail("声明为空");

        ActionDeclarationSet? set;
        try
        {
            set = JsonSerializer.Deserialize<ActionDeclarationSet>(json, Options);
        }
        catch (JsonException ex)
        {
            return ActionDeclarationParse.Fail($"声明不是合法 JSON: {ex.Message}");
        }

        if (set == null)
            return ActionDeclarationParse.Fail("声明反序列化为 null");

        if (set.SchemaVersion != ActionDeclarationSet.SupportedSchemaVersion)
            return ActionDeclarationParse.Fail(
                $"schemaVersion={set.SchemaVersion} 不受支持，本端只支持 {ActionDeclarationSet.SupportedSchemaVersion}");

        // owner 必须与被问的模块一致：否则一个模块可以借声明抢注别人的动作，
        // 而按钮那一侧完全看不出动作换了主人。
        if (!string.Equals(set.Owner, expectedOwner, StringComparison.OrdinalIgnoreCase))
            return ActionDeclarationParse.Fail($"owner 声明为 {set.Owner}，与被询问的模块 {expectedOwner} 不符");

        foreach (var action in set.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Id))
                return ActionDeclarationParse.Fail("存在缺少 id 的动作");
            if (action.Id != action.Id.ToLowerInvariant() || action.Id.Contains(' '))
                return ActionDeclarationParse.Fail($"动作 id 必须小写且无空格: {action.Id}");
            if (string.IsNullOrWhiteSpace(action.Command))
                return ActionDeclarationParse.Fail($"动作 {action.Id} 未声明 command");
            action.Owner = set.Owner;
        }

        var duplicate = set.Actions
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            return ActionDeclarationParse.Fail($"动作 id 重复: {duplicate.Key}");

        return new ActionDeclarationParse(set, null);
    }
}
