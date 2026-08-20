using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryAurora.Shell.Pages;

/// <summary>
/// 模块页面描述（协议 V1）。模块经指令总线返回本结构，Aurora 用自己的组件库渲染，
/// 模块不再构造任何 WPF 对象——见 b-Office/current/页面注册协议方案.md。
///
/// 本结构是**数据**，不含任何外观信息：模块只能选语义档位（<see cref="PageNode.Style"/>），
/// 具体长相由 Aurora 决定（DEC-005）。解析失败或版本不支持时整份描述作废，
/// 不做部分接受——半张页面比没有页面更难排查。
/// </summary>
public sealed class PageDescriptionSet
{
    /// <summary>当前支持的唯一 schema 版本。文本协议没有编译器保护，版本必须显式比对。</summary>
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    /// <summary>提供方模块名，与宿主注册表里的 owner 一致。</summary>
    public string Owner { get; init; } = "";

    public IReadOnlyList<PageDescription> Pages { get; init; } = [];
}

/// <summary>一个可停靠页面。</summary>
public sealed class PageDescription
{
    /// <summary>指令可寻址的窗口名，要求小写、无空格、进程内唯一。</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public PagePlacement Placement { get; init; } = new();

    public PageNode? Content { get; init; }
}

/// <summary>
/// 停靠位置。字段与 <c>ToolWindowDescriptor</c> 一一对应——那部分本来就是纯数据，
/// 不重新设计，只是换成文本承载。
/// </summary>
public sealed class PagePlacement
{
    /// <summary>left / right / top / bottom / center / tab；大小写不敏感。</summary>
    public string Side { get; init; } = "right";

    public double Ratio { get; init; } = 0.25;

    /// <summary>Side 为 tab 时并入哪个窗口的标签组。</summary>
    public string? TabTarget { get; init; }

    public bool Visible { get; init; } = true;

    public bool Singleton { get; init; } = true;
}

/// <summary>
/// 组件树节点。<see cref="Type"/> 不在 V1 组件集内时渲染为显式占位，
/// **不静默省略**——静默省略正是改协议要消灭的失败形态。
/// </summary>
public sealed class PageNode
{
    public string Type { get; init; } = "";

    /// <summary>节点在本页内的引用名，供 <see cref="PageInvoke"/> 取值与 enabledWhen 使用。</summary>
    public string? Id { get; init; }

    public string? Text { get; init; }

    /// <summary>语义档位（accent / ghost / danger / caption / secondary…），不是样式键。</summary>
    public string? Style { get; init; }

    /// <summary>stack 专用：vertical（缺省）或 horizontal。</summary>
    public string? Orientation { get; init; }

    /// <summary>stack 专用：间距档位 none / tight / normal。</summary>
    public string? Gap { get; init; }

    public IReadOnlyList<PageNode>? Children { get; init; }

    public IReadOnlyList<PageColumn>? Columns { get; init; }

    /// <summary>表格数据源；组件按需回调模块取数，数据不内联进描述。</summary>
    public PageDataSource? DataSource { get; init; }

    /// <summary>有外部副作用的动作。视图行为（筛选、排序、选中）由组件自理，不走这里。</summary>
    public PageInvoke? Invoke { get; init; }

    public PageViewOptions? View { get; init; }

    /// <summary>启用条件；目前只支持 { "selected": "&lt;节点 id&gt;" }。</summary>
    public PageEnabledWhen? EnabledWhen { get; init; }
}

public sealed class PageColumn
{
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>像素宽；"*" 表示占满剩余。</summary>
    public string? Width { get; init; }
}

/// <summary>组件取数：一条只读命令加固定参数，分页与筛选由组件追加。</summary>
public sealed class PageDataSource
{
    public string Command { get; init; } = "";

    public IReadOnlyDictionary<string, string>? Args { get; init; }
}

/// <summary>命令调用：参数值可以是字面量，也可以从视图状态取（<see cref="PageArgument.From"/>）。</summary>
public sealed class PageInvoke
{
    public string Command { get; init; } = "";

    public IReadOnlyDictionary<string, PageArgument>? Args { get; init; }
}

public sealed class PageArgument
{
    public string? Value { get; init; }

    /// <summary>形如 "commands.selected.name"：取 id 为 commands 的节点当前选中行的 name 列。</summary>
    public string? From { get; init; }
}

/// <summary>视图行为开关。这些**不产生命令**——组件自理。</summary>
public sealed class PageViewOptions
{
    public bool Filterable { get; init; }

    public bool Sortable { get; init; }

    /// <summary>none / single；V1 不支持多选。</summary>
    public string Selection { get; init; } = "none";
}

public sealed class PageEnabledWhen
{
    /// <summary>指定节点必须有选中行。</summary>
    public string? Selected { get; init; }
}

/// <summary>解析结果：成功带描述，失败带可直接写进日志的原因。</summary>
public readonly record struct PageDescriptionParse(PageDescriptionSet? Value, string? Error)
{
    public bool Ok => Value != null;

    public static PageDescriptionParse Fail(string reason) => new(null, reason);
}

public static class PageDescriptionReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 解析模块返回的描述。任何一步失败都返回 <see cref="PageDescriptionParse.Error"/>，
    /// 调用方据此**跳过该模块**并记 Warn，其余模块照常建页（协议 §1.5 隔离要求）。
    /// </summary>
    public static PageDescriptionParse Read(string? json, string expectedOwner)
    {
        if (string.IsNullOrWhiteSpace(json))
            return PageDescriptionParse.Fail("描述为空");

        PageDescriptionSet? set;
        try
        {
            set = JsonSerializer.Deserialize<PageDescriptionSet>(json, Options);
        }
        catch (JsonException ex)
        {
            return PageDescriptionParse.Fail($"描述不是合法 JSON: {ex.Message}");
        }

        if (set == null)
            return PageDescriptionParse.Fail("描述反序列化为 null");

        if (set.SchemaVersion != PageDescriptionSet.SupportedSchemaVersion)
            return PageDescriptionParse.Fail(
                $"schemaVersion={set.SchemaVersion} 不受支持，本端只支持 {PageDescriptionSet.SupportedSchemaVersion}");

        // owner 必须与被问的模块一致：否则一个模块可以借描述抢注别人的页面。
        if (!string.Equals(set.Owner, expectedOwner, StringComparison.OrdinalIgnoreCase))
            return PageDescriptionParse.Fail($"owner 声明为 {set.Owner}，与被询问的模块 {expectedOwner} 不符");

        if (set.Pages.Count == 0)
            return PageDescriptionParse.Fail("未声明任何页面");

        foreach (var page in set.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id))
                return PageDescriptionParse.Fail("存在缺少 id 的页面");
            if (page.Id != page.Id.ToLowerInvariant() || page.Id.Contains(' '))
                return PageDescriptionParse.Fail($"页面 id 必须小写且无空格: {page.Id}");
            if (string.IsNullOrWhiteSpace(page.Title))
                return PageDescriptionParse.Fail($"页面 {page.Id} 缺少标题");
            if (page.Content == null)
                return PageDescriptionParse.Fail($"页面 {page.Id} 缺少内容");
        }

        var duplicate = set.Pages
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            return PageDescriptionParse.Fail($"页面 id 重复: {duplicate.Key}");

        return new PageDescriptionParse(set, null);
    }
}
