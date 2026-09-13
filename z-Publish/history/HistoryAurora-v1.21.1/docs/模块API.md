# HistoryAurora 模块 API

模块版本：**1.20.3**；最低宿主：**HistoryVulcan 5.1.0**
（由 `b-Code-Studio/AuroraVersion.props` 的 `MinimumHistoryVulcanVersion` 单点声明）。

本文件是**总线面**合同：别的模块要在界面里露面、或要驱动布局时，看这一篇。

- 代码面（模块入口接口、窗格交接实现、前端执行器）在 `b-Office/current/技术合同.md`。
- 页面**怎么写**（组件、令牌、表格、面板、动作、选择通道）在同目录
  [`HistoryAurora_组件清单与用法.md`](./HistoryAurora_组件清单与用法.md)。
- AI 面（MCP 工具名与调用形状）由 MCP 服务封装，本文件不重复。

## 这个模块提供什么

Aurora 是宿主装载的**界面模块**：主窗口、场景、布局、控制台、命令集、弹窗、页面宿主。
指令域 `aurora`。

没装 Aurora 的宿主没有界面：`aurora.*` 与 `vulcan.ui.*` 都是未知指令。
宿主生命周期壳命令 `vulcan.app.show / hide / close / focusconsole` 仍由**宿主**注册，
执行体转到本模块——所以调这四条的模块不需要知道 Aurora 在不在。

## 模块怎么在界面里露面

两条路，二选一。

### 路一：描述化页面（页面注册协议 V1，推荐）

模块登记三条指令，Aurora 来问：

| 指令 | 作用 |
| --- | --- |
| `<域>.ui.describe` | 返回页面描述 JSON |
| `<域>.ui.actions` | 返回可被按钮绑定的动作声明 |
| `<域>.ui.data` | 按视图返回行数据 |

三条都是模块与界面之间的内部协议，**请一律声明 `HiddenReason`**，不要对远端暴露。
写法与可用组件见《组件清单与用法》。描述变了以后调 `aurora.ui.invalidate owner=<模块名>` 请求重拉本模块。

**时序保证**：Aurora 登记了 `aurora.host.ready`，宿主（5.1.3 起）装完全部模块后按命令名通知它整轮重拉。
所以你的 `describe` 一定在「全部模块都接上」之后才被问到——
**不要为「启动时 Aurora 问得太早」写重试，也不要在 `Attach` 里抢着 invalidate**。
`aurora.host.ready` 是宿主的生命周期回调，不由人或远端触发，模块也不要直接调。

### 路二：带注解的命令交出一个窗格

登记一条命令，把活对象（`UIElement`）放进 `CommandResult.Data`，并带上注解：

```text
ui.window = 窗格 id
ui.side   = left | right | top | bottom | center | tab
ui.title  = 标题（可省，回退命令摘要）
ui.ratio  = (0,1) 比例（可省，默认 0.25）
```

Aurora 在界面空闲时扫描注册表、执行该命令，把返回的窗格停靠进布局。

## 两条必须知道的界面规矩

**一格一页**：每个位置任何时刻只放一页，没有页签、没有页签切换、没有顶栏。
页面被显示或停到某处时，那个位置原来那一页被隐藏。
所以别在一次操作里连着 `show` 两个同位置的页、指望它们并排。
登记时位置被占着，新页是藏着的，要它露面走 `aurora.ui.show`。
`side=tab tabTarget=X` 仍然合法，意思是「与 X 同一个位置」；`aurora.ui.dock` 不接受 `pos=tab`。

**所有页面是同一种抽象**：命令集也是工具窗口，没有文档身份。
模块声明 `side=center` 拿到的一直是工具窗口，落位在中央区——**模块侧不需要为此改任何声明**。

## 场景

场景**只是一份布局**，不拥有页面。每个声明了页面的模块自动得到一个场景，id 就是模块名
（如 `HistoryJanus`，也认去掉 `History` 的简称）——**模块侧不需要改任何声明**。
第一次进入本模块的场景时露出本模块的页与常驻页，之后按用户离开时的样子恢复；
切到别的场景时页面对象不重建，状态都还在。控制台与命令集是常驻页，每个场景的初值里都有，
`tabTarget=console` 因此在任何场景里都成立。

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `aurora.scene.go` | `id` | 切到一个场景，恢复它上次的布局与显隐 |
| `aurora.scene.open` | `page` | 在**当前**场景里打开一页，不切场景 |
| `aurora.scene.list` | — | 列出全部场景（按使用频次）。**只读** |
| `aurora.scene.save` | `id`、`title` | 把当前露面的页与布局另存为场景并切过去 |
| `aurora.scene.reset` | `scene` | 场景回到默认形态；省略为当前场景 |
| `aurora.scene.delete` | `id` | 删除另存的场景；模块场景随模块装卸，删不掉 |

模块想把用户带到自己这里，调 `aurora.scene.go id=<模块名>`。

## 窗口与布局

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `aurora.ui.show` / `.hide` | `name` | 显示（隐藏则唤出、已显示则激活）/ 隐藏，状态保留 |
| `aurora.ui.dock` | `name`、`pos`、`ratio` | 停靠到某方位；`pos=center` 占中央区 |
| `aurora.ui.float` / `.floatstate` | `name`(、`state`) | 浮为独立顶层窗口 / 设其最大化状态 |
| `aurora.ui.max` / `.restore` | `name` | 最大化 / 退出最大化 |
| `aurora.ui.ratio` | `name`、`value` | 调整占主窗体比例 |
| `aurora.ui.autohide` | `name` | 切换自动隐藏 |
| `aurora.ui.reset` | `name` | 复位到注册时的默认位置 |
| `aurora.ui.windows` | — | 列出全部窗口及状态。**只读** |
| `aurora.ui.layouts` / `.layoutsave` / `.layoutload` / `.layoutreset` | (`name`) | 命名布局方案 |
| `aurora.ui.channels` | — | 列出选择通道、当前选中行与断链引用。**只读** |
| `aurora.ui.refreshdata` | `page`、`node` | 重新拉取页面数据，可按页或按节点 |
| `aurora.app.window` | `state` | 设置主窗口状态（如 `state=toggle`） |
| `aurora.app.about` | — | 关于对话框 |

## 面板

| 指令 | 参数 | 说明 |
| --- | --- | --- |
| `aurora.ui.panels` | — | 列出全部控制面板及窗口状态。**只读** |
| `aurora.ui.panelshow` | `id` | 显示面板（等价 `aurora.ui.show`） |
| `aurora.ui.panelset` | `panel`、`control`、`value` | 程序向面板控件回写值 |
| `aurora.ui.panelreload` | — | 重读面板 JSON 并原地重建；**新增面板需重启** |

## 弹窗与文件选择

`aurora.ui.dialog` 显示 Aurora 主题化弹窗，`kind` 取 `message` / `confirm` / `prompt` / `choice` / `content`。

```
aurora.ui.dialog kind=confirm title=确认 body="覆盖现有文件？" danger=true
```

`kind=choice` 的 `options` 是非空 `{label,value}` JSON 数组，成功结果的 `Message` 与 `Data` 都是所选 value。

`aurora.ui.selectfile` / `aurora.ui.selectdirectory` 选本地文件 / 目录。

## 控制台

`aurora.log.level`、`.source`、`.class`、`.keyword`、`.mute`、`.autoscroll`、`.focus`、
`.clear`、`.copy`、`.export`、`.prefill` 控制控制台的显示与过滤。
`aurora.command.history` 查指令历史（**只读**），`aurora.command.copyexample` 复制示例，
`aurora.command.runreadonly` 只执行只读指令、非只读的只填进输入框不执行。

**要读控制台日志请用 `diana.log.read`**：`aurora.log.snapshot` 是 Diana 的进程内只读提供者，不直接对远端暴露。

## 不要跨模块调的指令

以下声明了 `HiddenReason`，属界面内部协议或「只对坐在屏幕前的人有意义」：

`aurora.host.ready`（宿主生命周期回调）、`aurora.ui.describe` 侧的 `.actions` / `.data` / `.invalidate` /
`.invoke` / `.reloadpages` / `.reloadactions` / `.missing` / `.request` / `.requests`、
`aurora.log.snapshot`、`aurora.nav.open`、`aurora.module.hotreload` / `.reloadall`（界面自持页的组合指令）、
`aurora.preview.*`（组件测试页）。

## 已经删掉的

`aurora.scene.add` / `aurora.scene.remove` 在 1.20.0 删除，调用会得到「未知指令」；显隐用 `aurora.ui.show` / `hide`。

## 1.21.1 单模块页面刷新

aurora.ui.invalidate 在建页前刷新该模块动作声明。模块无需自行调用 reloadactions，也无需为热重载后的动作注册时序增加等待。
