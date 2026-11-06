using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Graph;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Components.Catalog;

/// <summary>协议：模块与界面之间的载荷与约定。节点和小组件之外、模块要在界面里露面必须知道的部分。</summary>
public static partial class ComponentCatalog
{
    /// <summary>XAML 令牌那一条的查询名；它的键清单由运行中的资源字典现取，不写在这里。</summary>
    public const string XamlSpecName = "xaml";

    private static IEnumerable<ComponentSpec> Protocols() =>
    [
        new()
        {
            Name = "page",
            Layer = ComponentLayer.Protocol,
            Model = typeof(PageDescriptionSet),
            Summary = "页面描述信封：模块注册只读指令 <域>.ui.describe 返回它，Aurora 来拉、建页、渲染",
            Fields =
            [
                new("schemaVersion", "int", "协议版本，当前只有 1；不符整份作废") { Required = true },
                new("owner", "string", "模块名，必须等于被询问的模块（如 HistoryJanus）") { Required = true },
                new("pages", "page[]", "页面列表，至少一页") { Required = true },
                new("pages.id", "string", "页 id：小写、无空格、本模块内唯一；aurora.ui.show / float name= 用它") { Required = true },
                new("pages.title", "string", "页标题") { Required = true },
                new("pages.scene", "string", "所属场景，必须等于 owner；省略按 owner 处理"),
                new("pages.placement", "object", "初次落位；之后以用户保存的场景布局为准"),
                new("pages.placement.side", "string", "停靠位置；tab 表示与 tabTarget 同一个位置") { Default = "right", Values = ["left", "right", "top", "bottom", "center", "tab"] },
                new("pages.placement.ratio", "number", "侧边停靠时占主窗体的比例 (0,1)") { Default = "0.25" },
                new("pages.placement.tabTarget", "string", "side=tab 时与哪一页同位；只跟随常驻页（如 console），别跟随别的模块的页"),
                new("pages.placement.visible", "bool", "进入本模块场景时是否露面") { Default = "true" },
                new("pages.placement.singleton", "bool", "单实例") { Default = "true" },
                new("pages.content", "node", "页面内容：一个节点（见各页面节点条目）") { Required = true },
            ],
            Example = """
                { "schemaVersion": 1, "owner": "HistoryMymodule", "pages": [
                  { "id": "items", "title": "条目", "scene": "HistoryMymodule",
                    "placement": { "side": "center" },
                    "content": { "type": "text", "text": "hello" } } ] }
                """,
            Rules =
            [
                "模块登记三条只读指令 <域>.ui.describe / <域>.ui.actions / <域>.ui.data，都声明 HiddenReason，不对远端暴露",
                "跨进程时 CommandResult.Data 不保形：同一份 JSON 也放进 Message；对象/数组优先取 Data，Data 退化为标量或字符串时回退 Message 中的对象/数组；失败结果不作为数据读取",
                "宿主装完全部模块后才通知 Aurora 整轮拉取，不要为「问得太早」写重试，也不要在 Attach 里抢着 invalidate",
                "描述变了调 aurora.ui.invalidate owner=<模块名>，它会连同动作声明一起重拉",
                "单模块刷新先取动作再替换页面内容；相同 id 的停靠位置、隐藏与分栏沿用用户布局，描述无效时保留旧页",
                "一格一页：每个位置任何时刻只放一页，后来的顶掉原来的；位置被占时新页藏着，要露面走 aurora.ui.show",
                "每个声明了页面的模块自动得到一个以模块名为 id 的场景；想把用户带过来调 aurora.scene.go id=<模块名>",
                "单模块描述失败只跳过该模块并记 Warn；未知节点渲染成显式占位并进缺件清单（aurora.ui.missing）",
                "Web 与桌面共用页面描述模型和解析器：字段名忽略大小写，数字字段可读字符串数字，缺省值一致；pages 不可为空或含 null，显式 placement=null 明确拒绝",
                "退役的独立 button / input / select 节点提示改用 panel / popup，不进入缺件清单；缺件按组件名忽略大小写归并，并列出引用页面",
                "描述里没有外观：不接受样式键与颜色。缺组件用 aurora.ui.request 申请，不要在模块里自建",
                "Web 字体由 aurora.css 统一管理并跟随 OneHistoryID：正文与控件使用西文罗马体、中文宋体，辅助文字同族，标题加粗；仅多行代码和控制台日志保留等宽。模块不声明字体、字号或字重",
                "Aurora 自持的命令集、模块管理、组件测试几页也走这套协议，b-Code-Studio/Shell/3-HostedPages/Views/HostedPageDescriptions.cs 是真在跑的范例",
            ],
        },
        new()
        {
            Name = "actions",
            Layer = ComponentLayer.Protocol,
            Model = typeof(ActionDeclarationSet),
            Summary = "动作声明：模块注册只读指令 <域>.ui.actions 返回它；按钮、行操作、单元格、泳道节点都只绑动作 id",
            Fields =
            [
                new("schemaVersion", "int", "协议版本，当前只有 1") { Required = true },
                new("owner", "string", "模块名") { Required = true },
                new("actions", "action[]", "动作列表") { Required = true },
                new("actions.id", "string", "动作 id：小写、无空格，建议以自己的域起头；这是按钮唯一记住的东西") { Required = true },
                new("actions.title", "string", "按钮缺省文字"),
                new("actions.command", "string", "真正执行的指令名，改名只改这里") { Required = true },
                new("actions.args", "map<string,string>", "参数；值里的 {控件id} 取同面板控件当前值，{selection.<通道>.<列>} 取通道，{列名} 在行操作里取被点那一行，{value} 取提交值，{node} 取被点泳道节点"),
                new("actions.danger", "bool", "危险档：按钮用 danger 样式；要不要再确认由指令自己经宿主 ConfirmPrompt 决定") { Default = "false" },
                new("actions.summary", "string", "一句话说明，作为悬停提示"),
                new("actions.clearOnSuccess", "string[]", "面板动作成功后清空这些控件 id；失败、输入或动作参数在等待期间改变、面板重建时保留草稿；不触发提交动作"),
            ],
            Example = """
                { "schemaVersion": 1, "owner": "HistoryMymodule", "actions": [
                  { "id": "mymodule.publish", "title": "发布", "command": "mymodule.release.publish",
                    "args": { "note": "{note}" }, "summary": "把当前候选发布出去" },
                  { "id": "mymodule.page.float", "title": "浮动", "command": "aurora.ui.float",
                    "args": { "name": "items" }, "summary": "浮成置顶小窗，再点还原" } ] }
                """,
            Rules =
            [
                "占位符引用了取不到的值时整条拒绝执行，不会把 {x} 原样发上总线",
                "声明了但指令不存在的动作进断链账（aurora.ui.actions）；按钮渲染成说明原因的牌子",
                "改了声明调 aurora.ui.invalidate 或 aurora.ui.reloadactions",
                "Web 与桌面共用动作声明解析器，字段名忽略大小写，版本可读字符串数字；动作 id 使用同一套小写、无空格规则，空动作数组合法",
                "actions 不可为 null 或包含 null，title=null 与无效 clearOnSuccess 数组明确拒绝；省略标题时按钮回退动作 id",
                "动作参数里的 null 按空串发送；占位符不含空白，面板控件引用与 clearOnSuccess 控件 id 忽略大小写",
                "动作声明独立于页面描述发现，没有页面的模块也可以提供动作；重拉时移除已退役声明，模块动作重名保留先登记者",
            ],
        },
        new()
        {
            Name = "selection",
            Layer = ComponentLayer.Protocol,
            Summary = "选择通道：页面之间唯一的接线方式。表格发布选中行，面板文本框发布当前值，别处按通道名取",
            Fields =
            [
                new("table.channel", "string", "表格把选中行发布到通道"),
                new("conversationList.channel", "string", "会话卡片把选中原始行发布到通道，刷新按 id 保留选中"),
                new("panel.textbox.channel", "string", "文本框把当前值发布到通道，列名固定 value"),
                new("panel.textbox.follows", "string", "<通道>.<列>：选中行一变就改写本框"),
                new("panel.button.enabledWhen.selected", "string", "<通道>：有选中行时按钮才可用"),
                new("{selection.<通道>.<列>}", "placeholder", "在动作 args、dataSource.args、optionsSource.args、switch.source 里取通道值"),
            ],
            Example = """{ "args": { "name": "{selection.mymodule.item.name}", "to": "{new-name}" } }""",
            Rules =
            [
                "通道名按最后一个点分成「通道 + 列」：通道名带点是常态，列名不含点",
                "通道名建议以自己的域起头；同名通道只认第一个声明方，第二个被拒并记 Warn",
                "Web 按页面声明顺序预登记通道，隐藏页与未打开分支也保留发布权；通道名大小写不敏感，重复声明在所属页提示并可查询",
                "引用了没人声明的通道等于按钮永远灰着；aurora.ui.channels 列出全部通道、当前值与断链引用",
                "Web 台账包含发布来源、当前行、引用方、断链和重复声明；模块刷新撤销原发布方后，仍在的发布方接替并复用当前控件值，退役通道清除旧选中",
                "改名这类动作同时要「改谁」和「改成什么」：选中行走 {selection.*}，输入框走 {控件id}",
            ],
        },
        new()
        {
            Name = "table.data",
            Layer = ComponentLayer.Protocol,
            Summary = "表格取数指令的返回：行数组（全量），或带版本的快照 / 增量",
            Fields =
            [
                new("[ {...} ]", "row[]", "最简形式：字符串字典数组，每次全量"),
                new("mode", "string", "snapshot 完整快照 / delta 增量") { Values = ["snapshot", "delta"] },
                new("revision", "string", "模块自定的不透明版本串，成功应用才推进；下次增量以 since=<revision> 传回") { Required = true },
                new("rows", "row[]", "snapshot 的全部行"),
                new("upserts", "row[]", "delta 的新增或更新行，按 rowKey 合并，省略字段保留，新键追加到末尾"),
                new("removes", "string[]", "delta 要删除的行键，不存在时忽略"),
            ],
            Example = """{ "mode": "delta", "revision": "r2", "upserts": [ { "id": "a", "name": "甲" } ], "removes": [ "b" ] }""",
            Rules =
            [
                "声明 rowKey 时所有行必须有非空唯一键；重复键、更新与删除冲突、非法载荷整批拒绝，旧内容与版本保持",
                "切换取数参数自动回全量；不要在 args 里占用保留参数 since",
                "同表请求串行、重复刷新合并，旧结果不会覆盖新选择",
                "列与行内容均未变化时复用原行，保留该行编辑草稿；目标行变化时还原草稿，刷新不提交编辑",
                "声明 rowKey 时按键保留选择；更新所选行会发布最新字段，删除所选行会清空选择",
                "数据在界面之外被改了，调 aurora.ui.refreshdata [page=] [node=] 按页或按节点重取，别一把全刷",
                "refreshdata 的页和节点 id 忽略大小写，没有匹配取数节点时明确失败并列出现有绑定；不刷新面板候选或控制台日志",
                "会跑 Git 之类重活的取数，用通道引用限定到单个对象；打开一页不该触发全库扫描",
            ],
        },
        new()
        {
            Name = "swimlane.data",
            Layer = ComponentLayer.Protocol,
            Model = typeof(SwimlaneDescription),
            Summary = "泳道取数指令的返回：泳道与节点，不含坐标、颜色、尺寸",
            Fields =
            [
                new("schemaVersion", "int", "当前只有 1") { Required = true },
                new("title", "string", "图标题"),
                new("lanes", "lane[]", "泳道") { Required = true },
                new("lanes.id", "string", "泳道 id") { Required = true },
                new("lanes.title", "string", "泳道标题"),
                new("lanes.tip", "string", "泳道头节点 id；没写 lane 的节点从这里沿首父回溯认领"),
                new("lanes.open", "bool", "是否仍在进行") { Default = "true" },
                new("nodes", "node[]", "节点") { Required = true },
                new("nodes.id", "string", "节点 id") { Required = true },
                new("nodes.parents", "string[]", "父节点；parents[0] 是首父，决定跟哪条泳道走") { Required = true },
                new("nodes.title", "string", "节点主文字"),
                new("nodes.subtitle", "string", "副文字"),
                new("nodes.tooltip", "string", "悬停提示"),
                new("nodes.lane", "string", "显式指定泳道"),
                new("nodes.tone", "string", "语义档位，不是样式键") { Default = "normal", Values = ["normal", "accent", "danger"] },
                new("nodes.order", "string", "同层排序键（时间戳、序号）；缺省按 id"),
                new("edges", "edge[]", "可省：父子关系已在 parents 里，Aurora 自己推，非首父画虚线"),
                new("edges.from", "string", "起点节点 id"),
                new("edges.to", "string", "终点节点 id"),
                new("edges.dashed", "bool", "虚线") { Default = "false" },
                new("selectAction", "string", "点击节点执行的动作 id，动作里用 {node} 取被点节点 id"),
                new("laneTitles", "bool", "是否在左侧画泳道标题栏；false 时整栏不占位，泳道从左边缘画起") { Default = "true" },
            ],
            Example = """
                { "schemaVersion": 1, "title": "4 个节点",
                  "lanes": [ { "id": "main", "title": "主线", "tip": "m2" }, { "id": "work", "title": "work", "tip": "w1" } ],
                  "nodes": [ { "id": "m1", "title": "m1", "parents": [] }, { "id": "w1", "title": "w1", "parents": [ "m1" ] },
                             { "id": "m2", "title": "m2", "parents": [ "m1", "w1" ], "tone": "accent" } ],
                  "selectAction": "mymodule.node.detail" }
                """,
            Rules = ["nodes / lanes 不接受 null，数组元素不能为 null；显式边必须有 from / to；无效描述整份作废，空节点数组合法"],
        },
        new()
        {
            Name = "panel.file",
            Layer = ComponentLayer.Protocol,
            Model = typeof(PanelDefinition),
            Summary = "独立面板文件：<数据目录>/panels/*.json，自成一个可停靠窗口，行与小组件同 panel 节点",
            Fields =
            [
                new("id", "string", "面板 id") { Required = true },
                new("title", "string", "窗口标题") { Required = true },
                new("visible", "bool", "启动时是否显示") { Default = "true" },
                new("side", "string", "停靠位置") { Default = "right", Values = ["left", "right", "top", "bottom", "center"] },
                new("ratio", "number", "占主窗体比例") { Default = "0.22" },
                new("rows", "row[]", "行，同 panel 节点") { Required = true },
                new("rows.mode", "string", "同 panel 节点") { Values = ["flex", "even"] },
                new("rows.widgets", "widget[]", "同 panel 节点") { Required = true },
            ],
            Example = """
                { "id": "release", "title": "发布", "side": "right", "ratio": 0.22, "rows": [
                  { "widgets": [ { "kind": "button", "action": "mymodule.publish", "text": "发布" } ] } ] }
                """,
            Rules = ["新页面优先写进 <域>.ui.describe 的 panel 节点；独立面板文件留给不属于任何模块的个人快捷面板",
                "文件允许注释、尾逗号和不区分大小写的字段名；每份经同一原生校验器检查，坏文件或重复 id 不阻断其他面板",
                "panelreload 重读并重建既有面板，不重排用户布局；新增面板在桌面重启或 Web 重新连接后注册，移除声明的旧窗口在重新进入前仍可保留",
                "隐藏独立面板首次 panelset 时先创建并显示；存在同名嵌入面板时优先回写独立窗口，不触发用户提交"],
        },
        new()
        {
            Name = "window",
            Layer = ComponentLayer.Protocol,
            Summary = "注解窗格：模块已有现成 WPF 控件时，登记一条带 ui.* 注解的指令，把控件放进 CommandResult.Data",
            Fields =
            [
                new("ui.window", "annotation", "窗格 id，模块内唯一；Aurora 自己加 owner 前缀") { Required = true },
                new("ui.side", "annotation", "停靠位置") { Values = ["left", "right", "top", "bottom", "center", "tab"] },
                new("ui.title", "annotation", "标题；省略回退指令摘要"),
                new("ui.ratio", "annotation", "侧边比例 (0,1)") { Default = "0.25" },
            ],
            Example = """{ "Annotations": { "ui.window": "myview", "ui.side": "center", "ui.title": "我的页面" } }""",
            Rules =
            [
                "能用页面描述就别走这条：描述协议不碰 WPF，外观自动跟随主题",
                "控件里只引用 Aurora 令牌与具名样式（见 xaml），一律 DynamicResource",
            ],
        },
        new()
        {
            Name = XamlSpecName,
            Layer = ComponentLayer.Protocol,
            Summary = "XAML 令牌与具名样式：注解窗格里的 WPF 控件只能引用这些键，键清单由运行中的资源字典现取",
            Fields =
            [
                new("Aurora.Brush.*", "brush", "颜色"),
                new("Aurora.Font.* / Aurora.Text.*", "font / style", "字体与文字样式；命令、路径、哈希用等宽"),
                new("Aurora.Button.*", "style", "按钮：Base 普通 / Accent 页面主操作（一页至多一个）/ Danger 不可逆 / Ghost 次要"),
            ],
            Example = """<Button Style="{DynamicResource Aurora.Button.Accent}" Content="执行" />""",
            Rules =
            [
                "一律 DynamicResource，不要 StaticResource：主题切换靠动态查找",
                "不写字面颜色（#RRGGBB、Colors.*），不自建 Style x:Key 与 ControlTemplate；缺成员用 aurora.ui.request 申请",
                "解析不到的键 WPF 不报错，只是静默退化成系统外观；浅色深色各看一次",
                "模块不要自己 new Window：弹窗用 aurora.ui.dialog，要置顶小窗用 aurora.ui.float",
            ],
        },
    ];
}
