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
aurora.ui.dialog kind=content title=预览 body="摘要" content="<大段正文>"
```

| kind | 用途 | 结果 |
|---|---|---|
| `message` | 一段说明 + 关闭 | 点关闭即成功 |
| `confirm` | 确认 / 取消；`timeout=` 秒后拒绝；`danger=true` 主按钮用危险档 | 取消或超时返回失败 |
| `prompt` | 带输入的确认 | 成功时 `Message`/`Data` 是输入文本 |
| `content` | 大段只读等宽正文（历史预览） | 点关闭即成功 |

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

你能决定的就这三件事：**取值键、标题、列宽**。列宽写像素数、`"*"`（占满剩余，
可多列共享），或者不写（按内容自适应）。颜色、圆角、行高、字号、表头、分隔线、
悬停与选中态、空态文案——都传不进来，这是有意的。

`columns` 整个省掉也行：列按各行键的首次出现顺序推断，标题即键名。
**行数与列数永远由数据决定**，不用也不能另外传计数。

行是字符串字典的数组：

```json
[ { "name": "alpha", "note": "第一条" }, { "name": "beta" } ]
```

某行缺的键自动补空串，不会让那一格变成一条看不见的绑定错误。

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

页面描述里的按钮也支持 `action`：

```json
{ "type": "button", "text": "发布", "invoke": { "action": "mymodule.publish" } }
```

仍写 `"command"` 的按钮不会被拦下，但渲染时会记一条 Warn——
"哪些按钮还没换成动作"因此是可查的事实，而不是某次走查的印象。

## 面板：三种小组件（1.6.0）

面板 JSON 放在 `<数据目录>/panels/*.json`。小组件只有三种，其余一律拒绝：

```json
{
  "id": "release",
  "title": "发布",
  "side": "right",
  "ratio": 0.22,
  "widgets": [
    { "kind": "text", "text": "填写说明后发布" },
    { "kind": "textbox", "id": "note", "label": "说明", "required": true },
    { "kind": "textbox", "id": "channel", "label": "通道",
      "mode": "select", "options": [ "stable", "beta" ], "value": "stable" },
    { "kind": "button", "action": "mymodule.publish", "text": "发布" }
  ]
}
```

| kind | 说明 |
|---|---|
| `text` | 一段说明文字，不参与取值 |
| `textbox` | `mode` 为 `input`（缺省，自由输入）或 `select`（在 `options` 里选） |
| `button` | 绑 `action`（动作 id）。**写指令名会校验失败** |

**破坏性变更（V1 → V2）**：`controls` 改名为 `widgets`；
`combo` / `check` / `slider` / `file` / `dir` / `number` / `label` **全部删除**，
不留兼容分支。`combo` 请改用 `textbox` + `mode=select`，`label` 改用 `text`。
按钮的 `command` 字段删除，改 `action`。

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
  "widgets": [
    { "kind": "textbox", "id": "depth", "label": "深度", "value": "2" },
    { "kind": "textbox", "id": "mode", "label": "模式", "mode": "select",
      "options": [ "全部", "仅主线" ] },
    { "kind": "button", "action": "mymodule.policy.save", "text": "保存" }
  ]
}
```

里面放的**就是面板那三种小组件**，声明与校验完全一致。弹出层改的是"什么时候占版面"，
不是"能放什么"。点别处即收起。

## 带候选的输入框（1.7.0）

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
- 挂在已有节点上的**能力**（如 `table.rowactions`、`input.suggest`）→ `SupportedCapabilities`。
  两份分开是有原因的：把能力名混进组件清单，会让 `type: "table.rowactions"`
  一边被判为"已支持"、一边渲染成缺件占位。
- 再在本文加一节用法。

仍然缺的组件用 `aurora.ui.request component=<名字> reason=<为什么>` 登记；
`aurora.ui.requests` 可查未交付的申请。

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
