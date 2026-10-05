using System.Text.Json.Serialization;

namespace HistoryAurora.Shell.Components.Catalog;

/// <summary>一条规格说的是哪一层的东西。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComponentLayer
{
    /// <summary>页面节点：页面描述里 <c>"type"</c> 的一个取值。</summary>
    Node,

    /// <summary>面板小组件：<c>panel</c> / <c>popup</c> 行里 <c>"kind"</c> 的一个取值。</summary>
    Widget,

    /// <summary>协议：模块与界面之间的一份载荷或一条约定（页面信封、动作声明、表格数据……）。</summary>
    Protocol,
}

/// <summary>
/// 一个字段的说明。<see cref="Path"/> 是 JSON 里的点分路径，相对于所在规格的模型；
/// 集合与字典自动展开，写 <c>columns.key</c> 与 <c>columns[].key</c> 都行。
/// </summary>
public sealed record ComponentField(string Path, string Type, string Description)
{
    public bool Required { get; init; }

    /// <summary>省略时的取值；null 表示省略即「没有」。</summary>
    public string? Default { get; init; }

    /// <summary>只接受这几个取值（大小写不敏感）；null 表示不限。</summary>
    public IReadOnlyList<string>? Values { get; init; }
}

/// <summary>
/// 一个组件或一份协议的自描述：用途、字段、示例与规矩。
///
/// **这是组件用法的唯一出处**。以前用法写在手工文档里，组件层谁都能改，文档却没人跟着改，
/// 删文档时清出来的过期内容比现行的还多。现在用法与实现同仓同提交，并由合同测试守住：
/// 渲染器认的节点、面板认的小组件都必须有规格；模型上每个可写字段都必须有说明；
/// 每份示例都必须能被真的解析和渲染（见 <c>ComponentCatalogContractTests</c>）。
/// 模块与 AI 经 <c>aurora.component.list</c> / <c>aurora.component.show</c> 读取。
/// </summary>
public sealed class ComponentSpec
{
    /// <summary>查询名。节点用 type 值（<c>table</c>），小组件用 <c>panel.&lt;kind&gt;</c>，协议自取（<c>page</c>）。</summary>
    public required string Name { get; init; }

    public required ComponentLayer Layer { get; init; }

    /// <summary>一句话：它是什么、什么时候用。</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// 字段对照的模型类。给了就由门禁核对：字段路径必须真实存在，模型上的可写字段必须有人说明。
    /// 同一个模型可以被几条规格分着说（各节点共用 <c>PageNode</c>）。
    /// </summary>
    [JsonIgnore]
    public Type? Model { get; init; }

    public IReadOnlyList<ComponentField> Fields { get; init; } = [];

    /// <summary>
    /// 照抄能跑的示例。节点是一个节点的 JSON，小组件是一个小组件的 JSON，有模型的协议是一份完整载荷。
    /// </summary>
    public required string Example { get; init; }

    /// <summary>字段表装不下的约定：失败形态、取舍、与别的组件怎么配合。每条一句。</summary>
    public IReadOnlyList<string> Rules { get; init; } = [];

    /// <summary>小组件在 JSON 里的 kind 值（<c>panel.textbox</c> → <c>textbox</c>）；其他层为 null。</summary>
    [JsonIgnore]
    public string? WidgetKind => Layer == ComponentLayer.Widget ? Name["panel.".Length..] : null;
}
