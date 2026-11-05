# HistoryAurora

> 宿主前端界面模块：窗口、场景、布局、控制台与主题

V2.0 工作区新增 `b-Code-Web/` 浏览器前端：`dotnet run --project b-Code-Web/HistoryAurora.Web.csproj`，
打开 `http://127.0.0.1:5280`。经本机 Portunus 读取真实模块页面、数据和动作，桌面恢复停靠布局和右侧控制栏，手机用胶囊切页。
中继返回的 Data 退化为字符串或标量时，复用 Message 中的 JSON 对象/数组，避免正常模块页面、动作与取数被误判为格式错误。
右下“页面调整”（或临时按住 Ctrl）可拖动页面、隐藏/恢复、调整停靠位置；分隔线可拖动调整比例，每个场景自动记住布局。
首次进入复用桌面已保存的场景布局，之后浏览器调整独立保存。Aurora 五个原生页面复用桌面声明；
页面调整中的“恢复桌面布局”可重新载入当前场景的桌面历史，覆盖旧浏览器排列，恢复后继续独立记忆。
页面登记共用桌面解析器，统一字段大小写、字符串数字、缺省值与归属校验；无效刷新保留已接受页面和草稿。
控制台增量读取宿主日志，通讯支持图片与可见消息已读，泳道共用桌面布局算法，隐藏组件延迟加载。
表格支持版本快照与增量合并，同表刷新串行并合并重复请求；未变行保留编辑与焦点，数据变更不自动提交草稿。
大表仅挂载视口附近的行，完整行集与选择独立保留；方向键、Home / End、PageUp / PageDown 可遍历全部数据。
表格断链操作逐项提示，危险样式与跨表参数沿用桌面；动作重载原位更新操作，保留选择与仍有效的编辑草稿。
命令集与控制台共用域筛选，类别只影响命令集；只读运行保留宿主真实结果，其他指令只预填名称。
控制台支持按动作 id 调用，参数仅取本条指令显式传入值；页面重载作用于当前浏览器，保留停靠布局和浮窗，重复重载串行处理。
按模块刷新只替换该模块的页面，保留其他页面草稿、选择与浮窗；动作声明可独立刷新，按钮及时更新且不重建输入框和表格。
执行中的面板动作在声明暂时移除再恢复后仍防止重复提交；来源提交失败可用原路径重试，迟到的选择结果保留较新输入。
选择通道按声明登记且忽略名称大小写，重复发布不能覆盖原选择；通道查询包含来源、当前值、引用方、断链与重复声明。
页面刷新仅触发匹配的数据节点，返回绑定数量，目标不存在时明确失败；面板回写同步全部可写副本，面板查询包含尚未挂载的声明与显示状态。
页面树结构错误在模块登记前整份拒绝；缺件查询覆盖隐藏页面与分支，退役节点提示改用控制面板，刷新声明后诊断同步更新。
独立快捷面板读取桌面 panels/*.json 并共用原生声明模型与校验器；支持显示、程序回写和重载，新增面板重新连接后注册。
容器沿用桌面的间距、填充和剩余视口规则；页面右键弹层与表格行菜单互不抢占，电脑就近展开，手机全屏返回。
通用弹窗和文件选择由当前浏览器接收；切换场景保留页面草稿与选择，命名布局可通过已有界面指令保存和恢复。
场景支持另存、删除、恢复、排序与收起，名称与布局保存在当前浏览器；模块重新连接不会复活手动删除的场景。
模块暂时离线不丢失页面原位与比例；无历史时的模块场景按桌面规则加入常驻页，模块自身页面优先占位。
弹出页面在电脑打开独立浏览器窗口，在手机显示全屏页面；搬移同一页，返回保留草稿、选择和滚动位置。
浮窗不改停靠布局，支持切换/收回；重开网页保留浮窗意图，在原位置点击恢复。当前为本地开发入口，公网接入仍由后续 Portunus 工作承担。
能力范围与验证入口见 `b-Office/现行约定.md` 的 Web 条目。

## 定位

HistoryAurora 是 HistoryVulcan 的界面模块，经 `RegisterFrontend` 登记为宿主唯一前端：主窗口、场景、布局、
控制台、命令集、弹窗与页面宿主都在这里。名字取自罗马黎明女神——它是体系对用户可见的那一面。

- 不是独立应用：启动与显隐走注册指令，发布只有模块包。没装 Aurora 的宿主没有界面。
- 别的模块不自建控件，而是以描述化页面协议注册页面，由 Aurora 的组件层渲染。
- 它的由来是把宿主前端进程自持的 39 条界面命令搬出宿主，让宿主只剩指令总线（MCP 工具一个不少）。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-026` |
| 角色 | 宿主模块（`kind=module`），宿主唯一前端 |
| 指令域 | `aurora` |
| 界面 | 本模块即界面 |
| MCP 投影 | `readonly` |
| 版本与宿主下限 | [`AuroraVersion.props`](./b-Code-Studio/AuroraVersion.props) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `ui` | `show` / `dock` / `float` / `layouts` / `panels` / `dialog` / `selectfile` … | 窗口、布局、面板、弹窗与文件选择 |
| `component` | `list` / `show` | 页面协议与组件用法的自描述 |
| `scene` | `list` / `open` / `go` / `save` / `reset` / `delete` | 场景 |
| `log` | `source` / `level` / `keyword` / `clear` / `export` … | 控制台筛选与导出 |
| `command` | `history` / `copyexample` / `runreadonly` | 命令集 |
| `app` | `about` / `window` | 关于与主窗口 |
| `host` | `ready` 等 | 宿主装载完成钩子（内部） |

模块怎么在界面里露面、有哪些组件、怎么写：`aurora.component.list` → `aurora.component.show name=<名>`（从 `page` 看起）；
`aurora.*` 指令的参数读注册自描述：`diana.docs.read domain=aurora`。本仓不写手工用法文档。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：真相在哪、组件层规矩、界面取舍、工程约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-Studio/Module/` | 模块工程与程序集元数据 |
| `b-Code-Studio/Shell/` | 界面源码，按层分目录（见「要点」） |
| `b-Code-Studio/eng/` | 构建与门禁脚本 |
| `b-Code-Verify/` | `Contracts` 合同测试、`ModuleSmoke` 模块装载冒烟 |
| `b-Office/` | `current/现行约定.md`，仅此一份 |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入；纳入 git，排除规则不得触碰 |

## 构建与验证

```powershell
dotnet restore .\HistoryAurora.sln --locked-mode -p:NuGetAudit=false
dotnet build .\HistoryAurora.sln -c Release --no-restore -p:NuGetAudit=false
dotnet test .\b-Code-Verify\Contracts\Contracts.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-Studio\eng\Test-QualityGate.ps1
```

宿主快照按相对路径解析（假定本仓与 `2026-023-HistoryVulcan` 在同一库根下）。
推送时 [`historyaurora-gate.yml`](./.github/workflows/historyaurora-gate.yml) 在 GitHub Actions 上复验。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布。

## 要点

`Shell/` 下只有五个层目录，编号越大越靠上，**依赖只能向下**；目录名即层，命名空间跟着目录走。

| 目录 | 命名空间 | 内容 |
| --- | --- | --- |
| `0-Neutral/` | `HistoryAurora.Shell.Neutral` | 指令面、日志、纯工具 |
| `1-Base/` | `.Base` | 页面外壳：停靠、顶栏、拖出拖入、页面合并、弹窗 |
| `2-Components/` | `.Components` | 风格令牌、表格、控制面板、泳道、页面描述与渲染器、组件目录（`Catalog/`） |
| `3-HostedPages/` | `.HostedPages` | Aurora 自己托管的页面，与模块页同一条路 |
| `4-Composition/` | `.Composition` | 装配根：`ShellWindow` 与内置指令组 |

- 门禁两道：`HistoryAurora.Module.csproj` 按层 `Include`（没分层的目录不参与编译）；`LayerContractTests` 守落点、命名空间与依赖方向。
- 改 `csproj` 时 `Link="%(RecursiveDir)…"` 必须吃掉层目录，否则资源名变成 `2-components/themes/…`：编译零征兆，运行期建窗抛 `IOException`。
- 页面 owner 由指令域推出（`History` + 域名首字母大写），与模块名无关。

## 保留内容

### logo与作者

- 作者：pinavia
- ![logo](./Logo.png)

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。
