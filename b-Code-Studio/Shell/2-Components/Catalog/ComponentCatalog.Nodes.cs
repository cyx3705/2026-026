using HistoryAurora.Shell.Components.Pages;

namespace HistoryAurora.Shell.Components.Catalog;

/// <summary>页面节点：<c>PageRenderer</c> 认的每一个 <c>type</c>。新增节点类型必须在这里补一条，否则门禁不过。</summary>
public static partial class ComponentCatalog
{
    private static ComponentField NodeType => new("type", "string", "节点类型，大小写不敏感") { Required = true };

    private static ComponentField NodeId => new("id", "string", "节点在本页内的引用名；表格取数刷新（aurora.ui.refreshdata node=）与选中行引用靠它");

    private static IEnumerable<ComponentSpec> Nodes() =>
    [
        new()
        {
            Name = "stack",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "顺序容器：子节点竖排或横排。表格、泳道（及内含它们的容器）自动拿剩余尺寸，其余按内容尺寸",
            Fields =
            [
                NodeType,
                NodeId,
                new("orientation", "string", "排列方向") { Default = "vertical", Values = ["vertical", "horizontal"] },
                new("gap", "string", "子节点间距档位；tight 与 normal 同值（等于页面内边距），只有 none 是 0") { Default = "normal", Values = ["none", "tight", "normal"] },
                new("children", "node[]", "子节点，按声明顺序排"),
                new("fill", "bool", "写在 stack 的**子节点**上：这一格占住剩余尺寸，后面的兄弟被推到末端（竖排即窗口底部）；在 grid 里不起作用") { Default = "false" },
            ],
            Example = """
                { "type": "stack", "gap": "normal", "children": [
                  { "type": "text", "text": "说明", "fill": true },
                  { "type": "text", "text": "贴底的一行", "style": "caption" }
                ] }
                """,
            Rules =
            [
                "页面本身不滚，只有组件内部滚：别在外面再包一层会无限长高的容器，也别给表格写死高度",
                "想把某一行固定到页底，给它前面那一格写 fill: true",
            ],
        },
        new()
        {
            Name = "text",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "一段说明文字，自动换行",
            Fields =
            [
                NodeType,
                new("text", "string", "正文；text 节点里是文字，panel / popup 里是标题或按钮文字") { Required = true },
                new("style", "string", "语义档位，不是样式键；text 用 caption / secondary（缺省正文），popup 用 accent 让开关按钮成主操作") { Values = ["caption", "secondary", "accent"] },
            ],
            Example = """{ "type": "text", "text": "选中左侧一行后再操作", "style": "secondary" }""",
            Rules = ["描述里不接受样式键和颜色，长相由 Aurora 决定"],
        },
        new()
        {
            Name = "table",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "表格：列声明 + 一条只读取数指令；外观（行高、字号、表头、分隔线、选中态）全在组件里",
            Fields =
            [
                NodeType,
                NodeId,
                new("columns", "column[]", "列；整个省掉时按各行键的首次出现顺序推断，标题即键名"),
                new("columns.key", "string", "取值键，对应行数据的字段名") { Required = true },
                new("columns.title", "string", "表头文字"),
                new("columns.width", "string", "列宽**权重**，不是像素：\"150\" 与 \"70\" 两列按 150:70 随页面缩放；\"*\" 相当于 200，不写相当于 120"),
                new("columns.cellAction", "string", "点击本列非空单元格执行的动作 id；参数里的 {列名} 取被点那一行"),
                new("columns.cellStyle", "string", "带 cellAction 的格画成什么：缺省链接（墨色字），button 画成行内小按钮，按钮上的字就是格里的值") { Values = ["button"] },
                new("dataSource", "object", "取数：组件按需调这条只读指令，数据不内联进描述"),
                new("dataSource.command", "string", "只读指令名，返回行集（字符串字典数组）或快照/增量载荷（见 table.data）") { Required = true },
                new("dataSource.args", "map<string,string>", "固定参数；值里可写 {selection.<通道>.<列>}，通道一变自动重取，取不到值时不发指令"),
                new("dataSource.deltaCommand", "string", "增量指令；拿到首份快照后改调它并自动追加 since=<revision>"),
                new("dataSource.rowKey", "string", "增量合并用的唯一行键；声明了 deltaCommand 就必须给"),
                new("channel", "string", "把选中行发布到这个选择通道（见 selection）；只有表格能声明"),
                new("rowActions", "rowAction[]", "行操作：一份声明同时给出行内按钮与右键菜单"),
                new("rowActions.action", "string", "动作 id；占位符 {列名} 默认取被点那一行的同名列") { Required = true },
                new("rowActions.title", "string", "按钮与菜单项文字"),
                new("rowActions.style", "string", "语义档位") { Values = ["danger"] },
                new("rowActions.inline", "bool", "是否同时放行内按钮；false 只进右键菜单，行操作多于两三条时用") { Default = "true" },
                new("rowActions.args", "map<string,arg>", "只在要写死值或跨节点取值时用；键是动作里的占位符名"),
                new("rowActions.args.value", "string", "写死的值"),
                new("rowActions.args.from", "string", "跨节点取值：<节点 id>.selected.<列>，取本页那张表当前选中行的某列"),
            ],
            Example = """
                { "type": "table", "id": "items", "channel": "mymodule.item",
                  "dataSource": { "command": "mymodule.ui.data", "args": { "view": "items" } },
                  "columns": [
                    { "key": "name", "title": "名称", "width": "*" },
                    { "key": "state", "title": "状态", "width": "80", "cellAction": "mymodule.item.toggle", "cellStyle": "button" }
                  ],
                  "rowActions": [
                    { "action": "mymodule.item.pin", "title": "固定" },
                    { "action": "mymodule.item.drop", "title": "移除", "style": "danger", "inline": false }
                  ] }
                """,
            Rules =
            [
                "行是字符串字典的数组；某行缺的键自动补空串。行数与列数永远由数据决定",
                "取数走宿主安静通道：不回显控制台、不入历史；失败记控制台 Warn，旧内容保留",
                "表格永远铺满可用宽度，不长横向滚动条；最后一列吸收拖动差额",
                "动作解析不到时表照画，上方列出断链原因；行内按钮列宽由 Aurora 估算，不接受声明",
                "不要自己拼 ListView / GridView / DataGrid",
            ],
        },
        new()
        {
            Name = "panel",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "控制面板：一组按行排的小组件（文字、文本框、按钮、开关、来源选择），按钮只绑动作 id",
            Fields =
            [
                NodeType,
                new("id", "string", "面板 id；aurora.ui.panelset panel= 与动作占位符的作用域都认它"),
                new("text", "string", "面板标题") { Default = "面板" },
                new("rows", "row[]", "面板的行，自上而下；行是声明出来的，不是排版算出来的") { Required = true },
                new("rows.mode", "string", "余量怎么分：flex 除一个可变元素外都停在最窄宽度、余量全给它；even 按最窄宽度等比放大") { Default = "flex", Values = ["flex", "even"] },
                new("rows.widgets", "widget[]", "本行的小组件，种类见 panel.* 各条") { Required = true },
            ],
            Example = """
                { "type": "panel", "id": "release", "text": "发布", "rows": [
                  { "widgets": [ { "kind": "textbox", "id": "note", "label": "说明", "flex": true },
                                 { "kind": "button", "action": "mymodule.publish", "text": "发布" } ] },
                  { "mode": "even", "widgets": [ { "kind": "switch", "id": "dry", "label": "只演练", "value": "true", "commitAction": "mymodule.dry" } ] }
                ] }
                """,
            Rules =
            [
                "放得下就不换行，放不下才折行；折出来的仍是同一行，行间横线不会多一条",
                "一行里两个 flex 只认第一个；一个都没写时可变的是最右边那个",
                "声明里任何一处不合规，整份面板跳过并记错误——半个面板比没有面板更危险",
                "小型交互控件只能放在 panel / popup 里，页面这一层没有 button / input / select 节点",
            ],
        },
        new()
        {
            Name = "popup",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "弹出层：低频编辑器不占版面，里面放的就是面板那几种小组件，点别处收起",
            Fields =
            [
                NodeType,
                new("id", "string", "弹出层 id，作用同面板 id"),
                new("text", "string", "开关按钮文字") { Default = "更多" },
                new("rows", "row[]", "与 panel 完全相同的行声明") { Required = true },
                new("trigger", "string", "怎么打开：button 自带一个按钮；context 不占版面、右键页面空白处唤起") { Default = "button", Values = ["button", "context"] },
            ],
            Example = """
                { "type": "popup", "text": "收录策略", "rows": [
                  { "widgets": [ { "kind": "textbox", "id": "depth", "label": "深度", "value": "2" } ] },
                  { "mode": "even", "widgets": [ { "kind": "button", "action": "mymodule.policy.save", "text": "保存" } ] }
                ] }
                """,
            Rules =
            [
                "context 接的是整页，写在树的哪个位置都一样；同一页最多接一个，第二个渲染成说明原因的牌子",
                "右键落在表格行上是行菜单，落在空白处才是 context 弹出层，两者不冲突",
                "context 看不见是它的代价：只用于页面上本来就有别的东西可看的低频编辑器",
            ],
        },
        new()
        {
            Name = "swimlane",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "泳道图：只描述节点与父子关系，排布、连线、颜色、尺寸全由 Aurora 决定",
            Fields =
            [
                NodeType,
                NodeId,
                new("dataSource", "object", "取数指令，返回泳道载荷（见 swimlane.data）；字段与表格的 dataSource 相同") { Required = true },
            ],
            Example = """{ "type": "swimlane", "id": "graph", "dataSource": { "command": "mymodule.ui.data", "args": { "view": "graph" } } }""",
            Rules = ["不显示滚动条，拖拽平移、滚轮导航；默认停在分支头"],
        },
        new()
        {
            Name = "grid",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "响应式栅格：只声明一列至少多宽，列数由可用宽度算出，窄了自动收列",
            Fields =
            [
                NodeType,
                NodeId,
                new("min", "number", "一列的下限宽度（像素）；列数不接受声明"),
                new("gap", "string", "间距档位，同 stack") { Default = "normal", Values = ["none", "tight", "normal"] },
                new("children", "node[]", "子节点"),
            ],
            Example = """{ "type": "grid", "min": 240, "children": [ { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ] }""",
            Rules = ["里面放表格时那一格照样拿到剩余高度，窄屏换行后仍可滚动"],
        },
        new()
        {
            Name = "switch",
            Layer = ComponentLayer.Node,
            Model = typeof(PageNode),
            Summary = "切换容器：一块版面上按某个通道值轮换显示一批组件（三个子页收进一页）",
            Fields =
            [
                NodeType,
                NodeId,
                new("source", "string", "按哪个值决定显示哪一支，写法 {selection.<通道>.<列>}；通常指向面板里声明了 channel 的选项框（列名固定 value）") { Required = true },
                new("children", "node[]", "各分支") { Required = true },
                new("case", "string", "写在 switch 的**子节点**上：本支对应 source 的哪个取值，大小写不敏感；第二支起必须写"),
            ],
            Example = """
                { "type": "switch", "id": "sections", "source": "{selection.mymodule.section.value}", "children": [
                  { "type": "text", "case": "规则", "text": "规则表" },
                  { "type": "text", "case": "历史", "text": "历史表" }
                ] }
                """,
            Rules =
            [
                "没有任何一支匹配（含通道还没有值）时显示第一支",
                "没被切到过的分支不取数；切走再切回是同一个实例，滚动、筛选、选中都还在",
                "source 写坏、没有分支渲染成说明原因的牌子；case 重复、漏写记 Warn。它们不算缺件",
            ],
        },
    ];
}
