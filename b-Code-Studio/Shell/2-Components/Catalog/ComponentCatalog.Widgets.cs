using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Components.Catalog;

/// <summary>面板小组件：<see cref="PanelWidgetKind"/> 的每一种。新增种类必须在这里补一条，否则门禁不过。</summary>
public static partial class ComponentCatalog
{
    private static ComponentField WidgetKind => new("kind", "string", "小组件种类，大小写不敏感") { Required = true };

    private static ComponentField WidgetMinWidth => new("minWidth", "number", "最窄宽度（像素，正数）；按钮与说明文字按字宽自动量，文本框不写按 120");

    private static ComponentField WidgetFlex => new("flex", "bool", "本行的可变宽度元素，余量全给它；even 行忽略") { Default = "false" };

    private static IEnumerable<ComponentSpec> Widgets() =>
    [
        new()
        {
            Name = "panel.text",
            Layer = ComponentLayer.Widget,
            Model = typeof(PanelWidget),
            Summary = "面板里的一段说明文字，不参与取值",
            Fields = [WidgetKind, new("text", "string", "正文；button 上则是按钮文字（不写用动作声明的标题）"), WidgetMinWidth, WidgetFlex],
            Example = """{ "kind": "text", "text": "填写说明后发布" }""",
        },
        new()
        {
            Name = "panel.textbox",
            Layer = ComponentLayer.Widget,
            Model = typeof(PanelWidget),
            Summary = "文本框：自由输入，或 mode=select 成轮换选项框（左键/空格轮换，右键列出全部候选）",
            Fields =
            [
                WidgetKind,
                new("id", "string", "控件 id，面板内唯一；动作参数里的 {id} 取它的当前值") { Required = true },
                new("label", "string", "左侧标签，自占一格并与输入区之间落一条竖线；写空串就不放这一格；不写退回 id"),
                new("mode", "string", "input 自由输入；select 在候选里选") { Default = "input", Values = ["input", "select"] },
                new("options", "string[]", "select 的静态候选；与 optionsSource 二选一"),
                new("optionsSource", "object", "select 的动态候选：一条只读指令返回行集，列名固定 value；与 options 二选一"),
                new("optionsSource.command", "string", "只读指令名") { Required = true },
                new("optionsSource.args", "map<string,string>", "固定参数；值里可写 {selection.<通道>.<列>}，通道一变就重取候选"),
                new("value", "string", "初值；select 时不在候选内则退回第一项"),
                new("multiline", "bool", "input 模式的多行正文输入，回车换行；启用后不执行 commitAction，须用显式按钮提交") { Default = "false" },
                new("commitAction", "string", "值提交时执行的动作 id，动作里用 {value} 取值：input 在回车或失焦时，select 选项一变就提交"),
                new("follows", "string", "跟随选择通道的某一列，写法 <通道>.<列>；选中行一变就整体改写本框"),
                new("channel", "string", "把本框当前值发布到这个选择通道，列名固定 value；switch 节点与取数参数可以引用它"),
                WidgetMinWidth,
                WidgetFlex,
            ],
            Example = """
                { "kind": "textbox", "id": "section", "label": "子页面", "mode": "select",
                  "channel": "mymodule.section", "options": [ "规则", "历史" ] }
                """,
            Rules =
            [
                "follows 的框会被覆盖；要一个不被覆盖的自由输入框就别写 follows",
                "程序回写（aurora.ui.panelset、跟随通道回填、候选重取）不触发 commitAction，模块可以放心回写显示态",
                "optionsSource 的候选里自己带一项「全部」之类的兜底：选项框没有清空动作",
                "重取候选会尽量留住选中项：还在就留着，否则退到 value，再否则第一项；通道还没值时候选保持原样",
            ],
        },
        new()
        {
            Name = "panel.button",
            Layer = ComponentLayer.Widget,
            Model = typeof(PanelWidget),
            Summary = "按钮：绑模块声明的动作 id，不接受指令名；执行期间自带运行态并阻止重复触发",
            Fields =
            [
                WidgetKind,
                new("action", "string", "动作 id（见 actions）；switch 也可用它代替 commitAction") { Required = true },
                new("icon", "string", "受控语义图标名，只属于按钮；模块不能提供 SVG、Path 或资源键") { Values = ["refresh-cw"] },
                new("enabledWhen", "object", "启用条件"),
                new("enabledWhen.selected", "string", "该选择通道有选中行时按钮才可用，没选中时悬停说明原因"),
                WidgetMinWidth,
                WidgetFlex,
            ],
            Example = """{ "kind": "button", "action": "mymodule.refresh", "text": "刷新", "icon": "refresh-cw", "enabledWhen": { "selected": "mymodule.item" } }""",
            Rules =
            [
                "动作缺失或写错时按钮渲染成写明原因的警示牌，aurora.ui.actions 列出断链",
                "面板成功操作会等本页数据刷新完成再结束运行态",
                "想让按钮调 Aurora 自己的能力（浮窗、刷新），声明一条指向 aurora.* 指令的动作即可，不需要新组件",
            ],
        },
        new()
        {
            Name = "panel.switch",
            Layer = ComponentLayer.Widget,
            Model = typeof(PanelWidget),
            Summary = "立即生效的布尔开关，点一下就以 {value}（true/false）提交动作",
            Fields =
            [
                WidgetKind,
                new("id", "string", "控件 id，面板内唯一") { Required = true },
                new("label", "string", "描述文字，画在开关本体内，只占一个排版格"),
                new("value", "string", "初值 true / false（或等价文本）"),
                new("commitAction", "string", "点击时执行的动作 id；与 action 至少写一个"),
                WidgetMinWidth,
                WidgetFlex,
            ],
            Example = """{ "kind": "switch", "id": "topmost", "label": "置顶", "value": "false", "commitAction": "mymodule.topmost" }""",
            Rules = ["只支持 id / label / value / action / commitAction；无标签时最小宽 48"],
        },
        new()
        {
            Name = "panel.sourcePicker",
            Layer = ComponentLayer.Widget,
            Model = typeof(PanelWidget),
            Summary = "来源输入框：可直接编辑，双击执行选择指令（选文件或目录）后回写并提交",
            Fields =
            [
                WidgetKind,
                new("id", "string", "控件 id，面板内唯一") { Required = true },
                new("label", "string", "左侧标签"),
                new("value", "string", "初值"),
                new("selectCommand", "string", "双击时执行的选择指令，如 aurora.ui.selectfile；会展开 {selection.<通道>.<列>} 与控件 id 占位符并按指令语法加引号") { Required = true },
                new("commitAction", "string", "可选：值提交时执行的动作 id；省略时只选择和保存控件值，由另一个显式按钮提交"),
                WidgetMinWidth,
                WidgetFlex,
            ],
            Example = """{ "kind": "sourcePicker", "id": "source", "label": "来源", "selectCommand": "aurora.ui.selectdirectory", "commitAction": "mymodule.source.set" }""",
            Rules = ["kind 也接受 source-picker / source"],
        },
        new()
        {
            Name = "panel.choices",
            Layer = ComponentLayer.Widget,
            Model = typeof(PanelWidget),
            Summary = "选项条：候选一字排开、点一下选中（墨色块），右键某一项弹出声明的菜单；放不下就折行",
            Fields =
            [
                WidgetKind,
                new("id", "string", "控件 id，面板内唯一；动作参数里的 {id} 取当前选中的值") { Required = true },
                new("label", "string", "左侧标签；写空串就不放这一格；不写退回 id"),
                new("options", "string[]", "静态候选；与 optionsSource 二选一"),
                new("optionsSource", "object", "动态候选：一条只读指令返回行集，value 列是值，可选 text 列是显示文字；与 options 二选一"),
                new("optionsSource.command", "string", "只读指令名") { Required = true },
                new("optionsSource.args", "map<string,string>", "固定参数；值里可写 {selection.<通道>.<列>}，通道一变就重取候选"),
                new("value", "string", "初值；不在候选内则选第一项"),
                new("channel", "string", "把选中的值发布到这个选择通道，列名固定 value；取数参数可以引用它"),
                new("commitAction", "string", "人点选一项时执行的动作 id，动作里用 {value} 取值"),
                new("menu", "object[]", "右键菜单项，自上而下"),
                new("menu.action", "string", "动作 id；参数里的 {value} 取被右键的那一项，不一定是选中的那一项") { Required = true },
                new("menu.title", "string", "菜单项文字；不写用动作声明的标题"),
                new("menu.style", "string", "语义档位") { Values = ["danger"] },
                WidgetMinWidth,
                WidgetFlex,
            ],
            Example = """
                { "kind": "choices", "id": "branch", "label": "分支", "flex": true,
                  "channel": "mymodule.branch", "options": [ "main", "feature-x" ],
                  "menu": [ { "action": "mymodule.branch.delete", "title": "删除", "style": "danger" } ] }
                """,
            Rules =
            [
                "与 select 选项框取数同形：重取候选尽量留住选中项，还在就留着，否则退到 value，再否则第一项；程序回写不触发 commitAction",
                "面板上任何动作成功后会重取全部候选：菜单里删掉、新增一项后不必另外刷新",
                "右键落在选项条上只开它自己的菜单，不再唤起页面的 context 弹出层",
                "选项条不是表格：候选多到要滚动、要排序筛选时用 table",
            ],
        },
    ];
}
