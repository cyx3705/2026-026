using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryAurora.Shell.Graph;

/// <summary>
/// 泳道图描述协议 V1（REQ-UI-010）。**模块只描述节点，不描述画法**：
/// 泳道怎么排、节点落在哪一列、边怎么走、什么颜色多大字号，全部由 Aurora 决定。
///
/// 这条边界是这一轮从 HistoryJanus 收回来的：泳道布局与绘制此前长在 Janus 的
/// <c>GraphLayout</c> / <c>GraphView</c> 里（合计约 890 行），Janus 因此既要懂 Git，
/// 又要懂 WPF 画布、视口裁剪与主题令牌。收进 Aurora 之后 Janus 只需回答
/// 「有哪些节点、谁是谁的父、有哪几条泳道」，那正是它本来就知道的事。
/// </summary>
public sealed class SwimlaneDescription
{
    /// <summary>当前支持的唯一 schema 版本。文本协议没有编译器保护，版本必须显式比对。</summary>
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    /// <summary>图上方的一行说明，例如「2026-026 · 4 条泳道 · 128 个节点」。</summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// 泳道。第 0 条视为主线。顺序有意义：靠前的泳道优先占据靠近主线的物理行。
    /// </summary>
    public IReadOnlyList<SwimlaneRef> Lanes { get; init; } = [];

    public IReadOnlyList<SwimlaneNode> Nodes { get; init; } = [];

    /// <summary>
    /// 显式边。留空时 Aurora 按 <see cref="SwimlaneNode.Parents"/> 自行推导——
    /// 父子关系已经在节点里说过一遍，再要求模块重复列一遍边只会多一处能对不上的地方。
    /// </summary>
    public IReadOnlyList<SwimlaneEdge>? Edges { get; init; }

    /// <summary>
    /// 点击节点时执行的动作 id（见 <see cref="HistoryAurora.Shell.Actions.ActionRegistry"/>）。
    /// Aurora 以 <c>{node}</c> 提供被点节点的 id 供动作参数取值。
    /// 这里同样**不接受指令名**：理由与面板按钮一致。
    /// </summary>
    public string? SelectAction { get; init; }
}

/// <summary>一条泳道。</summary>
public sealed class SwimlaneRef
{
    public string Id { get; init; } = "";

    /// <summary>泳道标题，显示在左侧行首。</summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// 泳道末端节点的 id。Aurora 从它沿首父回溯来认领本泳道的节点；
    /// 节点自带 <see cref="SwimlaneNode.Lane"/> 时以节点为准。
    /// </summary>
    public string? Tip { get; init; }

    /// <summary>未合并（仍在推进）的泳道，末端画一个端点。</summary>
    public bool Open { get; init; } = true;
}

/// <summary>一个节点。位置字段一个都没有——那是 Aurora 的事。</summary>
public sealed class SwimlaneNode
{
    public string Id { get; init; } = "";

    /// <summary>父节点 id，第 0 个为首父。首父决定节点跟着哪条泳道走。</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];

    /// <summary>节点首行，通常是短标识。</summary>
    public string Title { get; init; } = "";

    /// <summary>节点次行，通常是一句话摘要。</summary>
    public string? Subtitle { get; init; }

    /// <summary>悬停提示；为空时由 Aurora 用标题与副标题拼一条。</summary>
    public string? Tooltip { get; init; }

    /// <summary>显式泳道 id。不写则由首父回溯推断。</summary>
    public string? Lane { get; init; }

    /// <summary>语义档位：normal（缺省）/ accent / danger。**不是样式键**，映射写死在渲染器里。</summary>
    public string? Tone { get; init; }

    /// <summary>排序用的次序键（时间戳、序号）。同层节点按它定先后；缺省按 id。</summary>
    public string? Order { get; init; }
}

/// <summary>一条边。From 为父，To 为子。</summary>
public sealed class SwimlaneEdge
{
    public string From { get; init; } = "";

    public string To { get; init; } = "";

    /// <summary>非首父的边画成虚线。</summary>
    public bool Dashed { get; init; }
}

/// <summary>解析结果：成功带描述，失败带可直接写进日志的原因。</summary>
public readonly record struct SwimlaneParse(SwimlaneDescription? Value, string? Error)
{
    public bool Ok => Value != null;

    public static SwimlaneParse Fail(string reason) => new(null, reason);
}

public static class SwimlaneReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 解析泳道描述。校验口径与页面描述协议一致：版本不对或节点 id 重复整份作废。
    /// 节点为空**不是错误**——「这个项目还没有提交」是合法状态，画一张空图即可。
    /// </summary>
    public static SwimlaneParse Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SwimlaneParse.Fail("描述为空");

        SwimlaneDescription? value;
        try
        {
            value = JsonSerializer.Deserialize<SwimlaneDescription>(json, Options);
        }
        catch (JsonException ex)
        {
            return SwimlaneParse.Fail($"描述不是合法 JSON: {ex.Message}");
        }

        if (value == null)
            return SwimlaneParse.Fail("描述反序列化为 null");

        if (value.SchemaVersion != SwimlaneDescription.SupportedSchemaVersion)
            return SwimlaneParse.Fail(
                $"schemaVersion={value.SchemaVersion} 不受支持，本端只支持 {SwimlaneDescription.SupportedSchemaVersion}");

        foreach (var node in value.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                return SwimlaneParse.Fail("存在缺少 id 的节点");
        }

        var duplicate = value.Nodes
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            return SwimlaneParse.Fail($"节点 id 重复: {duplicate.Key}");

        var duplicateLane = value.Lanes
            .Where(lane => !string.IsNullOrWhiteSpace(lane.Id))
            .GroupBy(lane => lane.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateLane != null)
            return SwimlaneParse.Fail($"泳道 id 重复: {duplicateLane.Key}");

        return new SwimlaneParse(value, null);
    }
}
