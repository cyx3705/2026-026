# HistoryAurora 组件清单与用法

> 面向模块作者。本文列出 Aurora 前端提供的样式键与页面注册方式，以及**怎么用**。
> 颜色、间距、嵌入页结构和顶栏归属见
> [HistoryAurora_UI风格与嵌入页面规范](../current/HistoryAurora_UI风格与嵌入页面规范.md)。
> 组件的建设计划与调研依据见 `../history/前端组件计划表V1.0.md`；
> 长期约束见 `../current/技术合同.md`。

## 一分钟上手

模块不自己画界面骨架，只做两件事：**注册一个工具窗口**，**在页面里引用现成样式键**。

```csharp
// 1) 登记带 ui.window 注解的命令，活对象放 CommandResult.Data
registry.Register(new CommandDescriptor
{
    Name = "mymodule.ui.pane",
    Domain = "mymodule",
    CommandClass = "ui",
    Summary = "我的页面",
    Annotations = new Dictionary<string, string>
    {
        ["ui.window"] = "myview",
        ["ui.side"] = "center",
        ["ui.title"] = "我的页面",
    },
    Handler = CommandDescriptor.Sync(_ =>
        CommandResult.Ok("ok", new MyView(bus))),
});
```

```xml
<!-- 2) 页面里直接引用样式键，不要自己定义颜色 -->
<Button Style="{DynamicResource Aurora.Button.Accent}" Content="执行" />
<TextBlock Foreground="{DynamicResource Aurora.Brush.TextSecondary}" Text="说明文字" />
```

**一律用 `DynamicResource`，不要用 `StaticResource`。** 主题切换（浅色/深色）靠动态查找生效，
用静态引用的控件在切主题后颜色不会更新。

## 窗口注解字段

模块**不构造停靠描述符**——1.7.0 起停靠系统整体收成 Aurora 内部实现，
`ToolWindowDescriptor` / `IDockingService` / `DockSide` 都不在公开面上了（DEC-014）。
模块只在命令注解里写下这几个键，由 Aurora 认领：

| 注解键 | 说明 |
|---|---|
| `ui.window` | 窗口标识，模块内唯一。Aurora 会加 owner 前缀，不必自己拼模块名 |
| `ui.title` | 标签页显示名 |
| `ui.side` | `center` 中央工作区 / `left` / `right` / `bottom` / `tab` 并入标签组 |
| `ui.ratio` | 侧边停靠时占主窗体比例，如 `0.38` |

页面注册协议（`<域>.ui.describe`）里对应的是 `placement`，字段更全：
`side` / `ratio` / `tabTarget` / `visible` / `singleton`。
两条路选一条即可——注解适合"我已经有一个 WPF 控件"，
描述协议适合"我不想碰 WPF"（推荐，见 REQ-UI-003）。

**停靠位选择的一条经验**（来自 Janus 的实测教训）：`DefaultTabTarget` 指向**别的模块**提供的
窗口是不稳的——模块装载顺序不保证目标先到，先到就并入、没到就回退侧边停靠，
首次启动落位因此随机。要并标签组，就并到宿主自己注册的窗口（如控制台）；
不确定时用 `DockSide.Center`，它不依赖任何模块。

## 设计令牌（65 个）

只列最常用的。完整清单见 `Themes/AuroraTokens.xaml`。

### 颜色 `Aurora.Brush.*`

| 键 | 用途 |
|---|---|
| `Surface` / `SurfaceAlt` / `SurfaceHover` | 背景、次级背景、悬停态 |
| `TextPrimary` / `TextSecondary` / `TextDisabled` | 正文 / 辅助 / 禁用 |
| `Accent` / `AccentSoft` | 强调色、强调色浅底 |
| `ControlBorder` / `Hairline` | 控件描边、分隔细线 |

### 字体 `Aurora.Font.*`

`Family` 正文字体族、`MonoFamily` 等宽族、`Body` 正文号、`Small` 小号、`Mono` 等宽号。

命令文本、路径、哈希一律用 `Mono` / `MonoFamily`——变宽字体下对不齐，列表里尤其明显。

### 尺寸与间距

`Aurora.Space.ControlPad` 控件内边距、`Aurora.Size.Control` 标准控件高、`Aurora.Size.Tab` 标签高、
`Aurora.Radius.Inner` 内圆角、`Aurora.Shadow.Flyout` 浮层阴影。

## 具名样式（16 个）

### 按钮 `Aurora.Button.*`

| 键 | 何时用 |
|---|---|
| `Base` | 普通操作 |
| `Accent` | 页面主操作，一页**至多一个** |
| `Danger` | 删除、重置等不可逆操作 |
| `Ghost` | 工具条里的次要操作，无边框 |

### 分段条 `Aurora.Segment.*`

用于把一排相关控件收进一条带底的横条，视觉上成为一个整体。

| 键 | 目标类型 |
|---|---|
| `Bar` | Border——外层容器，先放它 |
| `Label` / `TextBox` / `ComboBox` / `Button` / `CheckBox` | 条内各类控件 |
| `Divider` | 条内分隔 |

```xml
<Border Style="{DynamicResource Aurora.Segment.Bar}">
  <StackPanel Orientation="Horizontal">
    <TextBlock Style="{DynamicResource Aurora.Segment.Label}" Text="项目" />
    <ComboBox Style="{DynamicResource Aurora.Segment.ComboBox}" />
    <Border Style="{DynamicResource Aurora.Segment.Divider}" />
    <Button Style="{DynamicResource Aurora.Segment.Button}" Content="刷新" />
  </StackPanel>
</Border>
```

> **已知缺件，正在补**：该家族目前没有 RadioButton / Toggle 成员，
> 即「互斥分段切换」（点一个亮一个的页面切换器）暂时无对应组件。Janus 的
> `OperationSegment` 就是为此自造的 40 行 ControlTemplate。
>
> `Aurora.Segment.Toggle` 是组件计划的 **P0**，且是「禁止自建组件」这条规矩的**解禁前置**——
> 它落地之前不会对模块执行该禁令。落地后请删除自造版本，外观不会变。

### 列表与表格

| 键 | 目标类型 | 说明 |
|---|---|---|
| `Aurora.GridHeader` | GridViewColumnHeader | 表头 |
| `Aurora.Item.Base` | ListViewItem | 行样式，含选中/悬停态 |

> **1.6.0 起改用 `AuroraTable`，别再自己拼表格。** 上面两个键仍在，
> 但它们已经是 `AuroraTable` 的内部实现——你要画表就用组件，见下文「表格」一节。
> 自己拼 `ListView` + `GridView` 会让这一页的行高与字号跟组件对不上，
> 而这种差异浅色下几乎看不出来。`DataGrid` 一直不在主题化范围内，仍然不要用。

### 轮换选项框 `AuroraOptionBox`（1.8.9）

页面描述里的 `select`、面板 `mode=select` 和 Aurora 自己的筛选器统一使用轮换选项框。
页面协议和面板 JSON 不变，模块仍然提供字符串候选项；控件内部基于 `Selector`，不增加公开程序集 API。

它只有当前状态文本，没有下拉箭头或独立按钮区域：左键、`Space` / `Enter`、右方向键切换下一项，
到末项后回到首项；左方向键反向切换，`Home` / `End` 跳到首项或末项。右键或 `Shift+F10` 打开全部候选，
当前项带选中标记，点击候选后立即生效。空集合不响应，单项保持原值，候选刷新时优先保留当前值，否则选中首项。

模块不需要直接构造该控件。**页面节点 `select` 已于 1.8.14 退役**，
现在只经面板的 `mode: "select"` 使用：

```json
{ "kind": "textbox", "id": "channel", "label": "通道", "mode": "select",
  "options": [ "stable", "beta" ] }
```

**1.8.17 起它还能当页面切换器用**：加一个 `channel`，它就把当前值发上选择通道，
页面里的 `switch` 容器跟着换掉下面那一批组件。见下文「一页里换一批组件」。

```json
{ "kind": "textbox", "id": "section", "label": "子页面", "mode": "select",
  "channel": "janus.section", "options": [ "Git 文件规则", "分支历史", "GitHub" ] }
```

下面这份是退役前的页面写法，仅作对照，**不再受支持**：

```json
{
  "type": "select",
  "id": "channel",
  "children": [
    { "type": "text", "text": "stable" },
    { "type": "text", "text": "beta" }
  ]
}
```

### 其他

`Aurora.ComboBox.ToggleButton`、`Aurora.TreeExpander`、`Aurora.ScrollBar.Thumb`——
一般不必直接引用，对应控件套用后自动生效。

## 弹窗 `aurora.ui.dialog`（1.2.0）

模块不要自己 `new Window`。前端独立之后，顶层窗口拿不到 `Aurora.*` 令牌，
`DynamicResource` 会静默退化成系统外观。弹窗由 Aurora 自持，自己合并主题字典，
并用与主窗体相同的自绘顶栏（无系统标题栏）。

写操作的宿主 `ConfirmPrompt` 在进程内也走同一组件：Aurora 启动时接管宿主确认通道。

```text
aurora.ui.dialog kind=message title=关于 body="……"
aurora.ui.dialog kind=confirm title=需要确认 body="覆盖现有文件？" danger=true defaultcancel=true
aurora.ui.dialog kind=prompt title=生成恢复提交 body="恢复提交说明" value="revert abc"
aurora.ui.dialog kind=choice title="选择目录" body="请选择要打开的目录" options='[{"label":"z 文档","value":"z-docs"}]'
aurora.ui.dialog kind=content title=预览 body="摘要" content="<大段正文>"
```

| kind | 用途 | 结果 |
|---|---|---|
| `message` | 一段说明 + 关闭 | 点关闭即成功 |
| `confirm` | 确认 / 取消；`timeout=` 秒后拒绝；`danger=true` 主按钮用危险档 | 取消或超时返回失败 |
| `prompt` | 带输入的确认 | 成功时 `Message`/`Data` 是输入文本 |
| `choice` | 从 `{label,value}` 列表选择；长列表自动滚动 | 成功时 `Message`/`Data` 是所选 `value` |
| `content` | 大段只读等宽正文（历史预览） | 点关闭即成功 |

`choice` 的 `options` 必须是非空 JSON 数组，每项同时给出非空 `label` 与 `value`。列表支持方向键、
Enter/Space、确认、取消和双击确认；畸形或空列表在开窗前直接返回失败。

模块不要再叠一层确认框。

**不要**把弹窗写进 `<域>.ui.describe` 的页面树。它不是停靠页，页面渲染器里没有
`dialog` 组件——写了只会变成显式占位。

## 表格：一律用 `AuroraTable`（1.6.0）

页面描述里写 `"type": "table"` 就够了。**不要自己拼 `ListView` + `GridView`**——
1.6.0 起表格只有一个实现，外观全在组件里。

```json
{
  "type": "table",
  "id": "rows",
  "dataSource": { "command": "mymodule.list" },
  "columns": [
    { "key": "name",  "title": "名称", "width": "160" },
    { "key": "note",  "title": "说明", "width": "*" }
  ]
}
```

你能决定的是**取值键、标题、列宽，以及可选的单元格动作 id**。颜色、圆角、行高、字号、表头、分隔线、
悬停与选中态、空态文案——都传不进来，这是有意的。

> **1.8.15 起列宽是「比例」而不是「像素」。**
>
> 表格永远铺满可用宽度：右边不留空扩展区，也不长横向滚动条。写进 `width` 的数字
> 变成**权重**——`"150"` 与 `"70"` 两列的比例仍是 150:70，但两列一起随页面宽度缩放。
> `"*"` 相当于权重 200，不写相当于 120。
>
> 已有页面不会报错，但列宽表现会变：原先写死 230px 的列，现在窄窗口下会跟着变窄，
> 宽窗口下会跟着变宽。想让某列相对更宽，把它的数字调大即可——**数字之间的比例才有意义，
> 绝对值不再有**。

表格还可以加一个 `channel`，把选中行发布出去给控制面板用——见下文「选择通道」。

`columns` 整个省掉也行：列按各行键的首次出现顺序推断，标题即键名。

### 可点击单元格（1.13.0）

某一列的值本身就是操作入口时，在列上写 `cellAction`：

```json
{
  "key": "status", "title": "状态", "width": "90",
  "cellAction": "janus.project.action"
}
```

单元格以按钮语义渲染，支持鼠标、Enter 和 Space，悬停说明取自动作的 `summary`。动作参数里的
`{name}` 默认取**被点那一行**的同名字段，不读取当前选中行；因此动作需要的隐藏字段可以留在行数据里，
不必都声明成可见列。空单元格不触发。动作缺失时表格仍显示，上方会明确列出断链原因。
**行数与列数永远由数据决定**，不用也不能另外传计数。

行是字符串字典的数组：

```json
[ { "name": "alpha", "note": "第一条" }, { "name": "beta" } ]
```

某行缺的键自动补空串，不会让那一格变成一条看不见的绑定错误。

> **1.8.16：表格在 `stack` 里会拿到剩余高度。**
>
> 此前 `stack` 是 `StackPanel`，它在排列方向上以无穷尺寸量子元素——放进去的表格
> 会把每一行都画出来，表现为**撑满整页且滚不动**（Janus 项目总览页实测）。
> 现在 `stack` 是按行分档的 Grid：`table` 与 `swimlane`（以及内含它们的容器）拿剩余尺寸，
> 其余按内容尺寸。你不需要声明任何东西，也没有可声明的东西。

1.8.6 起表格外围不再绘制背景、描边、圆角或裁剪，表格直接平铺在页面内容中；表头、行分隔线、
悬停态、选中态和空态仍由 `AuroraTable` 统一提供。表格可以与控制面板作为响应式栅格的同级项，
宽屏并列、窄屏上下排列，不要在调用方再包一层卡片。

## 控制面板层级（1.9.2）

`PanelView` 接受 `rows` 合同（见下方「面板的行」），不增加调用方外观参数。
面板只有一个 `SurfaceAlt` 浅色圆角外层，按钮、输入框和轮换选项框属于控制面板内部控件，
组件本身不再绘制圆角矩形外框。不添加阴影或第二层卡片；面板内的文字、标签、输入框、
轮换选项框和按钮均为扁平组件。普通操作使用 Ghost 按钮，危险操作继续使用 Danger 按钮。

分隔线：**行内相邻元素之间**一像素竖向渐隐线（含标签与它的输入区之间——控件不带边框，
那条线是两者唯一的分界），**声明行之间**一像素横向渐隐线；首尾不绘制。

面板上下的留白只有底板那 2px，与控制台顶部的过滤器工具条完全一致（1.9.2 起）——
两处用的本来就是同一份 `Aurora.Panel.Surface`。

Aurora 自带的组件测试页会以描述协议真实渲染路径展示 `stack`、三种文本、四种按钮、表格、普通与补全输入框、
轮换选项框、面板、泳道、响应式栅格和弹出层。该页面只使用 Aurora 内部预览指令，不依赖其他模块的数据或动作。

## 动作：按钮绑 id，不绑指令名（1.6.0）

**这是本轮唯一需要模块改代码的地方。**

面板按钮与泳道节点点击绑的是**动作 id**，不是指令名。为此模块要多注册一条命令：

```csharp
registry.Register(new CommandDescriptor
{
    Name = "mymodule.ui.actions",   // 后缀固定，Aurora 按它找人
    Domain = "mymodule",
    CommandClass = "ui",
    Summary = "动作声明",
    Readonly = true,
    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
        {
          "schemaVersion": 1,
          "owner": "HistoryMymodule",
          "actions": [
            {
              "id": "mymodule.publish",
              "title": "发布",
              "command": "mymodule.release.publish",
              "args": { "note": "{note}", "channel": "{channel}" },
              "danger": false,
              "summary": "把当前候选发布出去"
            }
          ]
        }
        """)),
});
```

`{note}` / `{channel}` 是**占位符**，Aurora 用同一面板里同名控件的当前值替换。
引用了不存在的控件时整条拒绝执行——不会把 `{note}` 原样发上总线。

**为什么要多这一条**：以前按钮直接写指令模板。你改一次指令名，所有写死该名字的按钮
同时变哑——点了没反应、没有报错、也没有任何地方记下这件事。
现在指令名只写在你自己的声明里，改名只改这一行，面板 JSON 和页面描述一个字不动。

反过来，你把声明删了或者写错了，Aurora **立刻**知道：
按钮不会渲染成按钮，而是一块写着原因的警示牌；`aurora.ui.actions` 还会把
"声明了但指令不存在"的动作单独列成断链账。

| 命令 | 用途 |
|---|---|
| `aurora.ui.actions` | 列出全部已声明动作，以及断链 |
| `aurora.ui.reloadactions` | 重新拉取声明并按新声明重建面板 |
| `aurora.ui.invoke action=<id> ...` | 按 id 执行一条动作（脚本与控制台入口） |
| `aurora.ui.channels` | 列出选择通道、当前选中行与断链引用 |
| `aurora.ui.refreshdata [page=] [node=]` | 重新拉取页面数据 |

按钮绑动作，写在**面板**里（页面节点 `button` 已于 1.8.14 退役）：

```json
{ "kind": "button", "action": "mymodule.publish", "text": "发布" }
```

按钮可以加受控语义图标；当前只支持 `refresh-cw`：

```json
{ "kind": "button", "action": "mymodule.refresh", "text": "刷新", "icon": "refresh-cw" }
```

模块不能提供 SVG、Path 或资源键。未知图标、或在非按钮组件上写 `icon`，会让整份面板按协议拒绝；
不写 `icon` 的旧声明完全不变。

> **1.8.14 破坏性变更：`button` / `input` / `select` 不再是页面节点。**
>
> 小型交互控件只能出现在**控制面板**里（`panel` 与 `popup`）。页面这一层只放容器
> （`stack` / `grid` / `switch`）、展示组件（`text` / `table` / `swimlane`）和复合组件（`panel` / `popup`）。
> 理由是排版：散落在页面各处的单个控件没有共同的对齐依据，每加一个都要重新决定
> 它跟谁对齐、跟谁分组。
>
> 页面里再写这三种，会渲染成一块写着原因的牌子并记一条 Warn。
> 它**不会**进组件申请台账——那是"还没做"的意思，而这是"决定了不放在这一层"。
>
> 随之退役的还有：页面按钮的 `invoke` / `enabledWhen`，以及 `input` 的 `suggest`。
> 替代路径——行级操作用表格的 `rowActions`；其余按钮放进 `panel`，用 `kind: "button"`
> 加动作 id 声明。
>
> **「表格选中行 → 按钮变可用」这条链路在 1.8.16 回来了**，见下文「选择通道」一节。
> 它比原来的 `enabledWhen` 多一件事：能跨页面。

## 选择通道：表格与面板的接线（1.8.16）

**「在表格里选中一行，另一个面板上的按钮才可用、输入框自动填上」——这一节讲怎么写。**

页面是一页一页渲染的，所以页内的节点 id 出不了这一页。通道名是**界面级**的，
因此表和面板可以在两个不同的页上。Janus 就是这么用的：项目表在中央的「项目总览」页，
操作面板在左侧的「项目操作」页。

三步，缺一不可：

```json
// 1) 表格声明：把选中行发到这个通道上。通道名建议以自己的域起头。
{
  "type": "table",
  "id": "projects",
  "channel": "janus.project",
  "columns": [ { "key": "name", "title": "项目", "width": "*" } ],
  "dataSource": { "command": "janus.ui.data", "args": { "view": "projects" } }
}
```

```json
// 2) 面板取值：输入框跟随通道的某一列；按钮按有无选中启停。
{
  "kind": "textbox", "id": "project-name", "label": "项目名",
  "follows": "janus.project.name"
},
{
  "kind": "button", "action": "janus.project.rename", "text": "改名",
  "enabledWhen": { "selected": "janus.project" }
}
```

```csharp
// 3) 动作声明：{selection.<通道>.<列>} 取选中行，不带前缀的取面板控件。
new { id = "janus.project.rename", title = "改名", command = "janus.proj.rename",
      args = new { name = "{selection.janus.project.name}", @new = "{project-name}" } }
```

**为什么第 3 步要分两种占位符。** 改名要同时知道「改谁」和「改成什么」。
`follows` 的输入框一开始等于选中行，但人把它改了之后就不再相等——
如果两边都从输入框取，改名就只能改成它自己。所以：
选中行走 `{selection.*}`，输入框走 `{控件id}`。

| 写法 | 放在哪 | 作用 |
|---|---|---|
| `"channel": "<名字>"` | 表格节点 | 把当前选中行发布到通道 |
| `"follows": "<通道>.<列>"` | 面板 `textbox` | 选中行一变就改写本框内容 |
| `"enabledWhen": { "selected": "<通道>" }` | 面板 `button` | 没选中时按钮禁用，悬停说明原因 |
| `{selection.<通道>.<列>}` | 动作 `args` | 取选中行的某一列 |

几条要知道的：

- **通道名按最后一个点分成「通道 + 列」。** 通道名自己带点是常态（`janus.project`），
  列名不含点，所以最后一个点是确定的分界。
- **同名通道只认第一个声明的表。** 第二张表会被拒绝并记一条 Warn——
  否则「按钮跟着哪张表走」就取决于建页顺序，而那个顺序不受任何东西保证。
- **`follows` 的框会被覆盖。** 它的定位是「显示当前选中的那一个，顺便可以改成别的」。
  要一个不被覆盖的自由输入框（比如提交描述），就别写 `follows`。
- **引用了没人声明的通道 = 按钮永远灰着**，界面上与「还没选中」一模一样。
  这条会进断链账：`aurora.ui.channels` 列出全部通道、当前有无选中、谁在引用，
  以及引用了但没人声明的那些。页面拉取完成时也会记一条 Warn。

## 取数可以重来（1.8.16）

表格与泳道图的数据不再只在建页时取一次。两条路：

**跟着选中走。** 取数参数里写通道引用，选中一变就自动重取：

```json
{
  "type": "table",
  "id": "history-rows",
  "dataSource": {
    "command": "janus.ui.data",
    "args": { "view": "history", "name": "{selection.janus.project.name}" }
  }
}
```

取不到值（还没选中）时**不发指令**，表格显示「请先选中一行」。
不这样做的话，没选中就去问「这个项目的历史」，拿回来的要么是错误要么是别人的历史。

**显式刷新。** 数据在界面之外被改了、界面这边收不到信号时用它：

```bash
aurora.ui.refreshdata page=rules      # 只刷这一页
aurora.ui.refreshdata node=rule-list  # 只刷这一个节点
aurora.ui.refreshdata                 # 全部
```

按钮绑它就是一个真的「刷新」按钮：

```json
{ "kind": "button", "action": "mymodule.rules.refresh", "text": "刷新" }
```
```csharp
new { id = "mymodule.rules.refresh", title = "刷新",
      command = "aurora.ui.refreshdata", args = new { page = "rules" } }
```

**刷新是有代价的，所以它按页、按节点分派，不是一把全刷。** 别的页上可能有一张
要扫 45 个仓库的表。同理，会跑 Git 的取数请一律用通道引用限定到单个项目——
「打开一个页签」不该触发全库扫描。

## 一页里换一批组件（1.8.17）

**「三个页签收进一页」这一节讲怎么写。** 分成两半：面板里的轮换选项框把当前值发上通道，
页面里的 `switch` 容器按它决定显示哪一支。

```json
{
  "type": "stack",
  "gap": "normal",
  "children": [
    {
      "type": "panel",
      "id": "janus-projops",
      "text": "项目操作",
      "rows": [ { "widgets": [
        { "kind": "textbox", "id": "section", "label": "子页面", "mode": "select",
          "channel": "janus.section",
          "options": [ "Git 文件规则", "分支历史", "GitHub" ] }
      ] } ]
    },
    {
      "type": "switch",
      "id": "sections",
      "source": "{selection.janus.section.value}",
      "children": [
        { "type": "table", "case": "Git 文件规则", "columns": [ … ] },
        { "type": "table", "case": "分支历史",     "columns": [ … ] },
        { "type": "table", "case": "GitHub",       "columns": [ … ] }
      ]
    }
  ]
}
```

- 发布方的列名**固定是 `value`**，不接受声明。允许自定义的话，引用侧写错一个字
  就是永远取不到值，而症状与「还没选中」完全一样。
- `case` 大小写不敏感。没有任何一支匹配（含通道还没有值）时显示**第一支**。
- 通道抢注规则与表格一致：同名通道只认第一个声明方，抢注失败的控件此后不再发布。

**它换的是组件，不是页面。** 三支各自还是普通的 `stack` / `table` / `panel`，
只是同一时刻只有一支挂在树上。收成三个页签是停靠层的事（三页同写 `side: "bottom"`），
收成一个控件是这一节的事。

三件你不必自己操心、但值得知道的事：

- **没被切到过的分支不取数。** 分支在渲染时就建好了（缺件与断链因此照常出账），
  但只有被切到时才挂上可视树。写成显隐切换的话三支的取数会在开页那一刻一起打出去——
  Janus 的规则落地状态那一支每次跑两条 `git ls-files`，没人看的两支不该付这个钱。
- **切走再切回来是同一个控件实例，也不重取。** 表格的滚动位置、筛选词和选中行
  都还在，那些正是人切走之前留下的上下文。
- **分支里的表格照样拿得到高度。** 容器的尺寸档位跟着里面走，不必额外声明。

`source` 写坏、一个分支都没有、`case` 重复、第二支起漏写 `case`——四种都会当场说出来
（前两种渲染成写明原因的牌子，后两种记 Warn）。它们**不算缺件**，不会进组件申请台账：
组件是有的，缺的是声明。

## 面板的行（1.9.2，破坏性）

**行是你声明出来的**，不是排版算出来的。`widgets` 换成 `rows`，每行 `{ mode, widgets }`：

```json
"rows": [
  { "widgets": [
    { "kind": "textbox", "id": "project-name", "label": "项目名", "flex": true,
      "follows": "janus.project.name" },
    { "kind": "button", "action": "janus.project.rename", "text": "改名" }
  ] },
  { "widgets": [
    { "kind": "textbox", "id": "new-project", "label": "新项目名", "flex": true },
    { "kind": "button", "action": "janus.project.create", "text": "新建" }
  ] },
  { "widgets": [
    { "kind": "textbox", "id": "commit-message", "label": "提交描述", "flex": true },
    { "kind": "button", "action": "janus.project.commit", "text": "提交当前项目" },
    { "kind": "button", "action": "janus.project.push", "text": "推送当前项目" }
  ] },
  { "mode": "even", "widgets": [
    { "kind": "textbox", "id": "section", "label": "子页面", "mode": "select",
      "channel": "janus.section", "options": [ "规则", "历史", "GitHub" ] }
  ] }
]
```

### 一行是怎么排出来的

1. **每个元素先有一个最窄宽度。** 按钮与说明文字按字宽量出来，**你不必注册**；
   文本框量不出来（空框的内容宽度是 0），因此按你写的 `minWidth`，不写就是 120。
2. **放得下就不换行。** 一行的元素按最窄宽度加起来还装得进可用宽度时不折行，
   余量按这一行的 `mode` 分掉。
3. **放不下才折行**，折成的每一截仍然属于**同一个声明行**：各自按这一行的 `mode`
   分配余量，而行与行之间那条横分隔线**不会**因为折行多出一条。

### 两种 mode

| mode | 余量怎么分 |
|---|---|
| `flex`（缺省） | 除一个可变元素外，其余停在最窄宽度，余量全给它。可变元素由 `"flex": true` 指定；一个都没写时是**最右边**那个 |
| `even` | 余量按最窄宽度**等比放大**——宽的还是宽、窄的还是窄，一起长 |

一行里写了两个 `flex` 只认第一个：拒绝整份声明太重，而「两个都变宽」没有一种分法说得出道理。

### 标签是一个独立的元素

文本框的标签自己占一格，参与最窄宽度的计算，并因此在它与输入区之间落下一条竖分隔线。
把 `label` 写成空串就不放这一格。

### 从 V2 迁过来

| V2 | V3 |
|---|---|
| `widgets: [...]` | `rows: [ { widgets: [...] } ]` |
| `orientation: "horizontal"` | 删掉——只有一套排版了 |
| 按钮 `inline: true` | 删掉——放进**同一个** `widgets` 数组就是同行 |
| 按钮不写 `inline`（自己一行） | 各自单独一个 `rows` 项 |
| `required: true` | **删掉**，见下 |

`required` 退役的理由要写清楚：它的校验是**全局**的——面板上任何一个必填框为空，
**每个**按钮都拒绝执行，而报出来的错说的是那个框的名字，看上去与你点的按钮毫无关系。
Janus 因此一直不敢用它，Mercury 三个数字框全写了它却共用同一批按钮，
等于把整块面板锁在「三个都填了」上。参数缺失请交给你自己那条指令的 `Required` 去报——
报出来的还是那条指令的话。

## 面板：三种小组件（1.6.0）

面板 JSON 放在 `<数据目录>/panels/*.json`。小组件只有三种，其余一律拒绝：

```json
{
  "id": "release",
  "title": "发布",
  "side": "right",
  "ratio": 0.22,
  "rows": [
    { "widgets": [ { "kind": "text", "text": "填写说明后发布" } ] },
    { "widgets": [
      { "kind": "textbox", "id": "note", "label": "说明", "flex": true },
      { "kind": "textbox", "id": "channel", "label": "通道",
        "mode": "select", "options": [ "stable", "beta" ], "value": "stable" }
    ] },
    { "mode": "even", "widgets": [
      { "kind": "button", "action": "mymodule.publish", "text": "发布" }
    ] }
  ]
}
```

| kind | 说明 |
|---|---|
| `text` | 一段说明文字，不参与取值 |
| `textbox` | `mode` 为 `input`（缺省，自由输入）或 `select`（在候选里选）；候选写 `options`（静态）或 `optionsSource`（取数，1.9.2，二选一）；可加 `follows`（跟着通道取值）与 `channel`（把自己的值发上通道，1.8.17） |
| `button` | 绑 `action`（动作 id）。**写指令名会校验失败**；可加 `enabledWhen` |

三种 kind 都可以写 `minWidth`（最窄宽度，像素）与 `flex`（本行的可变宽度元素）。

**破坏性变更（V2 → V3，1.9.2）**：`widgets` 换成 `rows`；
`orientation`、按钮的 `inline`、文本框的 `required` **全部删除**，不留兼容分支。
迁移对照见上一节。

**破坏性变更（V1 → V2）**：`controls` 改名为 `widgets`；
`combo` / `check` / `slider` / `file` / `dir` / `number` / `label` **全部删除**。
`combo` 请改用 `textbox` + `mode=select`，`label` 改用 `text`。
按钮的 `command` 字段删除，改 `action`。

### 候选可以是活的：optionsSource（1.9.2）

`options` 是写死在描述里的常量数组，表达不了「候选从一条指令来」，
更表达不了「上一级变了这一级跟着换一批」。两级联动下拉写成这样：

```json
{ "kind": "textbox", "id": "domain", "label": "域", "mode": "select",
  "channel": "mymodule.domain",
  "optionsSource": { "command": "mymodule.ui.data", "args": { "view": "domains" } } },
{ "kind": "textbox", "id": "class", "label": "类", "mode": "select",
  "optionsSource": {
    "command": "mymodule.ui.data",
    "args": { "view": "classes", "domain": "{selection.mymodule.domain.value}" }
  } }
```

形状与表格的 `dataSource` 是**同一套**：一条只读指令加固定参数，返回行集，
**列名固定 `value`**；`args` 的值里可以写 `{selection.<通道>.<列>}`，引用的通道一变就重取。

几条要知道的：

- **与 `options` 二选一。** 两个都写会校验失败——同时写的话，界面上看到的那一份
  取决于取数回来的时机，而两次打开可能不一样。
- **候选里要自己带一项「全部」之类的兜底。** 轮换选项框没有「清空」这个动作，
  不给一个不筛的选项，人筛了就退不回来。
- **通道还没有值时候选保持原样**，不会被清空——「还没轮到我」与「这一级真的没有候选」
  在界面上必须看得出区别。
- **重取会尽量留住选中项**：还在新候选里就留着，否则退到 `value`，再否则第一项。
- **取数失败不静默**：候选留在原样，日志里记一条带指令名的 Warn。

声明里任何一处不合规，**整份面板跳过**并记一条错误——面板是一组互相取值的控件，
少掉其中一个则按钮拿到的参数就是错的，半个面板比没有面板更危险。

## 泳道图：只描述节点（1.6.0）

`"type": "swimlane"` 的节点用 `dataSource` 指一条只读命令，该命令返回泳道描述：

```json
{
  "schemaVersion": 1,
  "title": "2026-026 · 4 条泳道 · 128 个节点",
  "lanes": [
    { "id": "main", "title": "主线", "tip": "m3" },
    { "id": "work", "title": "ai/work", "tip": "w1", "open": true }
  ],
  "nodes": [
    { "id": "m1", "title": "m1", "subtitle": "初始提交", "parents": [] },
    { "id": "m2", "title": "m2", "parents": [ "m1" ] },
    { "id": "w1", "title": "w1", "parents": [ "m1" ] },
    { "id": "m3", "title": "m3", "parents": [ "m2", "w1" ] }
  ],
  "selectAction": "mymodule.node.detail"
}
```

**描述里没有坐标、颜色和尺寸，也不该有。** 泳道怎么排、节点落在哪一列、
边怎么走、什么颜色多大字号，全部由 Aurora 决定。你只需要说清楚：
有哪些节点、谁是谁的父、有哪几条泳道。

- `edges` 可以整个省掉——父子关系在 `parents` 里已经说过一遍，
  Aurora 自己推，非首父自动画虚线。同一件事说两遍就会有对不上的一天。
- `parents[0]` 是首父，决定节点跟着哪条泳道走。
- 节点可以用 `lane` 显式指定泳道；不写就从泳道的 `tip` 沿首父回溯认领。
- `tone` 只接受语义档位 `normal` / `accent` / `danger`，不是样式键。
- 点击节点执行 `selectAction` 声明的动作，Aurora 以 `{node}` 提供被点节点的 id。

## 行操作：一份声明，两个出口（1.7.0）

表格的 `rowActions` 同时给出**行内按钮**与**右键菜单**。不用挑一个，也不用写两遍。

```json
{
  "type": "table",
  "id": "items",
  "columns": [
    { "key": "name",  "title": "名称", "width": "*" },
    { "key": "state", "title": "状态", "width": "80" }
  ],
  "dataSource": { "command": "mymodule.ui.data", "args": { "view": "items" } },
  "rowActions": [
    { "action": "mymodule.item.pin",  "title": "固定" },
    { "action": "mymodule.item.drop", "title": "移除", "style": "danger" },
    { "action": "mymodule.item.sync", "title": "重新同步", "inline": false }
  ]
}
```

- 动作里的占位符 `{name}` **默认取被点那一行的同名列**。行操作的作用域天然就是一行，
  不必绕道 `commands.selected.name` 那种跨节点路径。要写死值或跨节点取值时才写 `args`。
- `inline: false` 表示只进右键菜单。行操作超过两三条时用它——
  全塞进行内会把数据列挤没，而那正是这个组件要解决的问题的反面。
- 落点仍是**动作 id**（见下一节），不接受指令名。
- 解析不到动作时表照画，上面多出一块写清是哪几条断了。少一个按钮是看不出来的。
- 行内按钮列的宽度由 Aurora 估算，**不接受列宽声明**——它不是数据。

## 弹出层：低频编辑器不占版面（1.7.0）

```json
{
  "type": "popup",
  "text": "收录策略",
  "rows": [
    { "widgets": [ { "kind": "textbox", "id": "depth", "label": "深度", "value": "2" } ] },
    { "widgets": [
      { "kind": "textbox", "id": "mode", "label": "模式", "mode": "select",
        "options": [ "全部", "仅主线" ] }
    ] },
    { "mode": "even", "widgets": [
      { "kind": "button", "action": "mymodule.policy.save", "text": "保存" }
    ] }
  ]
}
```

里面放的**就是面板那三种小组件**，声明与校验完全一致。弹出层改的是"什么时候占版面"，
不是"能放什么"。点别处即收起。

### 右键弹出：连按钮都不占版面（1.9.1）

`trigger` 决定怎么打开它。缺省 `button` 就是上面那样，自带一个按钮；写 `context` 则
**整个不占版面**，改由右键页面空白处唤起：

```json
{
  "type": "popup",
  "id": "dock-add",
  "text": "加入扩展坞",
  "trigger": "context",
  "rows": [ { "widgets": [
    { "kind": "textbox", "id": "value", "label": "名称 / 指令 / 路径", "flex": true },
    { "kind": "button", "action": "mymodule.add", "text": "加入" }
  ] } ]
}
```

**浮层本体是同一个**——圆角、阴影、收起时机全部沿用按钮式那一份，`trigger` 换的只是开关。

几条要知道的：

- **它接的是整页，不是它声明的那一格。** 因此写在页面树的哪个位置都一样；
  声明的那一格只留一个不占版面的空位。
- **同一页最多接一个。** 右键只有一次，接第二个的话「弹出哪一个」取决于建页顺序，
  而那个顺序不受任何东西保证。第二个渲染成写明原因的牌子（与选择通道的抢注规则同理）。
- **和表格的行操作菜单不冲突，也不需要协调。** 右键落在表格的**行**上是行菜单
  （`rowActions`），落在空白处才是这个浮层——表格在空白处会主动放弃自己的菜单，
  事件继续往上冒。
- **看不见是它的代价。** 版面上没有任何东西提示「这里右键有内容」，所以它只适合
  「页面上本来就有别的东西可看」的低频编辑器。是主操作就老老实实留按钮式。
- `trigger` 写别的值会退回 `button` 并记一条 Warn——静默当成 button 的症状是
  「右键怎么点都没反应」，而版面上确实多了个按钮，看上去像组件本来就长这样。

## 带候选的输入框（1.7.0，页面节点已于 1.8.14 退役）

> `input` 不再是页面节点，`suggest` 随之退役，`input.suggest` 也已从可申请能力表里移除。
> 下面是退役前的写法，仅作对照。控制面板暂不提供补全；确有需要请提组件申请。

```json
{ "type": "input", "suggest": "commands" }
```

按指令名自身的结构分段补全：`<域>.<类>.<方法> 参数=值`。
光标在第一段补指令名，在其后补参数名，写了 `=` 就补该参数的允许值。
候选与控制台 Tab 补全**来自同一个会话**，因此不会出现"控制台补得出来、这里补不出来"。

当前只支持 `suggest: "commands"`。声明了但界面暂时没有补全会话时，
退回普通输入框并在控制台记一条 Warn——不会静默变成一个普通框。

## 响应式栅格（1.7.0）

```json
{
  "type": "grid",
  "min": 240,
  "gap": "normal",
  "children": [ { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ]
}
```

**只声明"一列至少多宽"，不声明列数。** 列数由可用宽度算出，窄了自动收列。
接受列数的话，窄窗口下的横向溢出会原样回来。

## 公共组件区：缺什么可以自己来加（1.7.0）

组件区是**公共的**。以下几处任何项目都可以直接提交组件，不必先提需求书再等排期：

| 位置 | 放什么 |
|---|---|
| `b-Code-Studio/Shell/Table/` | 表格与行操作 |
| `b-Code-Studio/Shell/Panels/` | 面板与小组件 |
| `b-Code-Studio/Shell/Graph/` | 图形类（泳道图等） |
| `b-Code-Studio/Shell/Widgets/` | 通用小组件（弹出层、补全输入框、栅格） |
| `b-Code-Studio/Shell/Themes/AuroraControls.xaml` | 具名样式 |

进公共区要守五条，每一条都是本仓已经踩过的坑：

1. **外观封在组件里。** 调用方只给数据与尺寸这类形状信息，不给颜色圆角字号。
   不要留"默认值可覆盖"的入口——只要存在覆盖入口，第一个赶时间的页面就会用它，
   之后这一页永远长得不一样。
2. **颜色字号一律取令牌**（`Aurora.Brush.*` / `Aurora.Font.*`），一律 `DynamicResource`。
   注意 `AuroraControls.xaml` **不合并** `AuroraTokens.xaml`：
   在里面写 `BasedOn="{StaticResource Aurora.Text.Caption}"` 会在解析期抛
   `XamlParseException`，整份控件字典随之失效。跨字典就把 setter 内联，令牌照走动态。
3. **组件自带控件字典**：构造时调 `AuroraComponentResources.Ensure(this)`。
   浮动窗口与弹出层是独立的可视树，拿不到主窗体的控件字典；
   而 WPF 对解析不到的 `DynamicResource` **不抛异常**，只是不套用——
   表现为"只有这一块长得不一样"，浅色下几乎看不出来。
4. **有动作就绑动作 id**，不接受指令名（见「动作」一节）。
5. **带一条契约测试**，并在测试的文档注释里写清**这个组件为什么存在**——
   哪种失败形态促成了它。断言容易重写，那段理由重建不出来。

登记两处，缺件台账才会自动出账：

- 新的**节点类型** → `PageRenderer.SupportedComponents`；
- 挂在已有节点上的**能力**（如 `table.rowactions`、`table.channel`、`panel.follows`、
  `panel.enabledwhen`、`panel.rows`、`panel.minwidth`、`panel.flex`、`panel.optionssource`、
  `table.datasource.selection`）→ `SupportedCapabilities`。
  两份分开是有原因的：把能力名混进组件清单，会让 `type: "table.rowactions"`
  一边被判为"已支持"、一边渲染成缺件占位。
- 再在本文加一节用法。

仍然缺的组件用 `aurora.ui.request component=<名字> reason=<为什么>` 登记；
`aurora.ui.requests` 可查未交付的申请。

## 界面自己也用这套协议（1.9.0）

从 1.9.0 起，Aurora **自持的四页**（命令集、指令详情、模块管理、组件测试）都是页面描述，
与你的页面走同一个解析器、同一个渲染器、同一个包边。

这对你有两个实际影响：

**一、看得到一份真实的范例。** `b-Code-Studio/Shell/Views/HostedPageDescriptions.cs`
就是四页的完整描述，配套取数在 `HostedPageData.cs`。它不是为文档写的示例，
是真的在跑的那一份。

**二、缺件不再只有你会撞上。** 此前只有模块作者会撞到「这个组件没有」，
而你没有权限补，只能提申请等排期；界面自己不用这套协议，就永远不会先撞到墙。
现在界面自己先疼。1.9.0 改造时当场撞出的取舍全部记在技术合同 REQ-UI-052 里——
包括为了不加组件而**删掉的界面功能**（两级联动下拉、搜索框补全）。

### 已知的空转声明：表格的 `view`

`view.filterable` / `view.sortable` / `view.selection` 在 schema 里能写、解析器也认，
但**当前没有任何实现**（REQ-UI-054）。写了不会报错，也不会有任何效果。

在它被实现或从 schema 移除之前，**不要写这一项**，也不要按它规划页面：
需要筛选就把筛选参数带进 `dataSource`（见「取数可以重来」），在取数那一侧做。

## 规矩

1. **不自建组件。** 模块 XAML 里**不得出现 `Style x:Key=` 与 `<ControlTemplate>`**。
   需要的成员不存在时，向 Aurora 提缺件，不要在自己仓里造一个——自造版本不跟随主题演进，
   最终表现为「只有这一页长得不一样」。实测三个模块的全部自造（2 样式 + 4 模板）
   都源自同一个缺件，补齐家族即可全部消除。
2. **不写字面颜色。** 页面里出现 `#RRGGBB` 或 `Colors.*` 即为违规——深色主题下必然出错。
3. **一律 `DynamicResource`。** 见开头说明。
4. **业务入口走命令总线。** 按钮点击应调 `CommandBus`，不要直接改服务状态或窗口状态。
   这样同一能力在控制台、脚本、MCP 里都能用，不会变成只有点得到的「断头指令」。
5. **不要依赖别的模块的窗口存在。** 见上文 `DefaultTabTarget` 的经验。

## 破坏性变更：`Shell.*` → `Aurora.*`（模块必须适配）

前端从 HistoryVulcan 切到 Aurora 时，**65 个令牌与 16 个具名样式的键全部改名**：
`Shell.Brush.Accent` → `Aurora.Brush.Accent`，依此类推。家族结构与语义不变，只换前缀。

**不提供 `Shell.*` 别名，没有弃用期。** 别名意味着每个组件维护两套键、每次演进同步两处，
且无法判断某个模块实际用的是哪一套；而"弃用期"在实践中等于永久保留。一次性断掉，
适配才有明确的完成标志。（依据：Aurora DEC-005）

### 这个变更不会让你的模块崩溃——这正是危险之处

`DynamicResource` 找不到键时 WPF **不抛异常**，只是不套用该值。所以未适配的页面
**不会报错，而是静默退化成 WPF 默认外观**——灰按钮、系统字体、和其余页面明显不一致。

不要指望运行时告诉你哪里漏了。用检查器：

```bash
# 残留旧键
grep -rn "Shell\.[A-Za-z.]*" <你的模块仓> --include=*.xaml

# 自建组件（本轮起禁止，见下）
grep -rn 'Style x:Key=\|<ControlTemplate' <你的模块仓> --include=*.xaml

# 字面颜色
grep -rnE '#[0-9A-Fa-f]{6}|Colors\.' <你的模块仓> --include=*.xaml
```

三条都返回空，才算适配完成。

### 适配步骤

1. 全仓替换 `Shell.` → `Aurora.`（键名前缀，注意别误伤 `ShellWindow` 等类型名）；
2. 删除自建样式与模板，改用组件库对应成员；
3. 跑上面三条检查；
4. 浅色与深色各目视一次——静默退化不会报错，只能看。
# 1.15.0 取数不回显，程序回写不提交

页面表格与 `optionsSource` 的取数走宿主安静通道：不回显控制台、不入历史。取数不是操作——
一次用户动作会连带触发本页全部表格重取，那些回显会把真正的结果顶出屏幕。取数失败仍然写日志。

**程序回写控件值不再触发 `commitAction`。** `aurora.ui.panelset`、跟随通道回填与候选重取
都算程序回写，不是用户编辑。模块因此可以放心把控件回写成一个「显示态」，不会被反手当成
一次用户选择提交回来——先前那条回环的表现是：模块回写「（不写）」，控件把它当成用户选了
「（不写）」提交回去，整表被清空，而用户什么都没点。

选项框右键随时可开完整菜单，不需要先左键激活控件。

# 1.12.19 下拉提交与来源选择占位符

`mode: "select"` 的文本框在选项变更时立即执行 `commitAction`（`{value}` 为当前项）。`sourcePicker` 的 `selectCommand` 会展开 `{selection.<通道>.<列>}` 和控件 id 占位符；替换值按指令语法加引号，这样「属性整备（改名）」不会拆成多段参数。

# 1.12.0 新增控制面板组件

控制面板 `kind` 新增 `switch` 与 `sourcePicker`。`switch` 的 `value` 使用 `true/false`（或等价文本），点击后立即以动作声明中的 `{value}` 提交；`sourcePicker` 以 `selectCommand` 指定文件或目录选择命令，以 `commitAction` 提交回写值。普通 `textbox` 也可声明 `commitAction`，在回车或失焦时提交 `{value}`。这些控件均由 Aurora 渲染，模块页面只提交描述式 JSON。

# 1.12.2 开关布局修正

带 `label` 的 `switch` 将描述文字直接显示在开关本体内，一个声明只占一个排版单元格；`mode: "even"` 行会按开关内容自然宽度均布控件。无标签开关仍保留 48px 的最小宽度。
