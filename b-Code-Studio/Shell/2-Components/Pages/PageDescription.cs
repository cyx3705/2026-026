using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryAurora.Shell.Components.Pages;

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

    /// <summary>声明初值所属的模块场景。省略时兼容为 owner；不授权修改其他场景。</summary>
    public string? Scene { get; init; }

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

    /// <summary>stack / grid 专用：间距档位 none / tight / normal。tight 与 normal 同值（等于页面内边距，REQ-UI-122），只有 none 是 0。</summary>
    public string? Gap { get; init; }

    public IReadOnlyList<PageNode>? Children { get; init; }

    public IReadOnlyList<PageColumn>? Columns { get; init; }

    /// <summary>
    /// panel / popup 专用：面板的行，与控制面板同一套契约（REQ-UI-060）。
    ///
    /// 1.9.2 之前这里是平铺的 <c>widgets</c>，行由 <c>inline</c> 的位置副产出来。
    /// 现在行是一等结构：每行自己说是均布还是可变宽度，元素自己说最窄多宽。
    /// </summary>
    public IReadOnlyList<HistoryAurora.Shell.Components.Panels.PanelRow>? Rows { get; init; }

    /// <summary>
    /// table 专用：行操作（REQ-UI-011）。一份声明同时给出行内按钮与右键菜单，
    /// 页面作者不必在两种控件之间挑一个、挑完还要各写一遍参数。
    /// </summary>
    public IReadOnlyList<PageRowAction>? RowActions { get; init; }

    /// <summary>
    /// input 专用：候选来源。目前只支持 <c>commands</c>（指令名与参数分段补全）。
    /// 声明了但当前拿不到补全会话时退回普通输入框，并记一条 Warn——不静默。
    /// </summary>
    public string? Suggest { get; init; }

    /// <summary>
    /// table 专用：把当前选中行发布到这个**选择通道**（REQ-UI-041）。
    ///
    /// 通道名是界面级的，因此别的页面上的控制面板也能按它取值——
    /// 「选中一行 → 另一页的按钮变可用」只能这样表达，页内节点 id 出不了这一页。
    /// 建议以自己的域起头（<c>janus.project</c>）；同名通道只认第一个声明方。
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>
    /// switch 专用：按哪个值决定显示哪一支，写法 <c>{selection.&lt;通道&gt;.&lt;列&gt;}</c>
    /// （REQ-UI-046）。通常指向控制面板里一个声明了 <c>channel</c> 的轮换选项框。
    ///
    /// 通道当前没有值时显示第一支——页面一打开就得有东西可看，
    /// 「等一个可能永远不来的值」在界面上与「这块坏了」没有区别。
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// switch 子节点专用：本支对应 <see cref="Source"/> 的哪一个取值。大小写不敏感。
    /// 不写的话这一支只能作为兜底被显示（没有任何一支匹配上时用第一支）。
    /// </summary>
    public string? Case { get; init; }

    /// <summary>
    /// popup 专用：怎么把它打开（REQ-UI-056）。
    ///
    /// <c>button</c>（缺省）自带一个按钮，看得见「这里还有东西」；
    /// <c>context</c> 不占版面，右键页面空白处弹出——适合「低频、且页面上本来就有别的东西可看」
    /// 的编辑器。同一页最多接一个 <c>context</c>：右键只有一次，接第二个的话
    /// 「弹出哪一个」就取决于建页顺序，而那个顺序不受任何东西保证。
    ///
    /// 写了别的值按缺省处理并记一条 Warn——静默当成 button 的症状是「右键怎么点都没反应」。
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>grid 专用：一列的下限宽度（像素）。列数由可用宽度算出，**不接受声明**。</summary>
    public double? Min { get; init; }

    /// <summary>表格数据源；组件按需回调模块取数，数据不内联进描述。</summary>
    public PageDataSource? DataSource { get; init; }

    /// <summary>有外部副作用的动作。视图行为（筛选、排序、选中）由组件自理，不走这里。</summary>
    public PageInvoke? Invoke { get; init; }

    public PageViewOptions? View { get; init; }

    /// <summary>启用条件；目前只支持 { "selected": "&lt;节点 id&gt;" }。</summary>
    public PageEnabledWhen? EnabledWhen { get; init; }
}

/// <summary>
/// 一条行操作。落点与按钮一样是**动作 id**：指令改名由模块自己的
/// <c>&lt;域&gt;.ui.actions</c> 吸收，页面描述一个字不动（REQ-UI-009）。
///
/// 动作里的占位符 <c>{列名}</c> 默认取**被操作那一行**的同名列——
/// 行操作的作用域天然就是一行，让它去引用"某个节点的选中行"是绕远路。
/// <see cref="Args"/> 只在需要写死值或跨节点取值时才用。
/// </summary>
public sealed class PageRowAction
{
    public string? Action { get; init; }

    public string Title { get; init; } = "";

    /// <summary>语义档位：danger 表示危险动作。</summary>
    public string? Style { get; init; }

    /// <summary>是否同时在行内放一个按钮；false 表示只进右键菜单。缺省 true。</summary>
    public bool Inline { get; init; } = true;

    public IReadOnlyDictionary<string, PageArgument>? Args { get; init; }
}

public sealed class PageColumn
{
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>像素宽；"*" 表示占满剩余。</summary>
    public string? Width { get; init; }

    /// <summary>
    /// 点击本列非空单元格时执行的动作 id。动作参数默认从被点行的同名字段解析，
    /// 与 <see cref="PageRowAction"/> 使用同一套动作台账和占位符规则。
    /// </summary>
    public string? CellAction { get; init; }
}

/// <summary>组件取数：一条只读命令加固定参数，分页与筛选由组件追加。</summary>
public sealed class PageDataSource
{
    public string Command { get; init; } = "";

    /// <summary>可选增量命令；收到首次完整快照后使用，自动追加 since 参数。</summary>
    public string? DeltaCommand { get; init; }

    /// <summary>增量更新所用的唯一行键。</summary>
    public string? RowKey { get; init; }

    public IReadOnlyDictionary<string, string>? Args { get; init; }
}

/// <summary>
/// 按钮的落点。二选一：
/// <list type="bullet">
///   <item><see cref="Action"/>——模块声明的动作 id，**推荐**。指令改名不影响按钮；</item>
///   <item><see cref="Command"/>——直接写指令名。模块改名时这里会静默失效，
///         因此渲染器会记一条 Warn，让"哪些按钮还没换成动作"是可查的。</item>
/// </list>
/// </summary>
public sealed class PageInvoke
{
    /// <summary>动作 id（见 <c>&lt;域&gt;.ui.actions</c>）。与 <see cref="Command"/> 同时给出时以本项为准。</summary>
    public string? Action { get; init; }

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
            if (page.Scene != null && !string.Equals(page.Scene, set.Owner, StringComparison.OrdinalIgnoreCase))
                return PageDescriptionParse.Fail($"页面 {page.Id} 只能声明自己的模块场景 {set.Owner}");
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
