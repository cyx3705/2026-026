# HistoryAurora

> 宿主前端界面模块：窗口、场景、布局、控制台与主题

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
| `ui` | `show` / `dock` / `max` / `layouts` / `panels` / `dialog` / `selectfile` … | 窗口、布局、面板、弹窗与文件选择 |
| `scene` | `list` / `open` / `go` / `save` / `reset` / `delete` | 场景 |
| `log` | `source` / `level` / `keyword` / `clear` / `export` … | 控制台筛选与导出 |
| `command` | `history` / `copyexample` / `runreadonly` | 命令集 |
| `app` | `about` / `window` | 关于与主窗口 |
| `host` | `ready` 等 | 宿主装载完成钩子（内部） |

模块在界面里露面的两条路（描述化页面 / 带注解命令交出窗格）见 [模块 API](./b-Office/package/模块API.md)，
可用组件见 [组件清单与用法](./b-Office/package/HistoryAurora_组件清单与用法.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office/current/项目概览.md) | 目标、范围与状态 |
| [技术合同](./b-Office/current/技术合同.md) | 现行需求与架构 |
| [有效决策](./b-Office/current/有效决策.md) | 仍然有效的关键决策 |
| [验证合同](./b-Office/current/验证合同.md) | 验证层级、命令与证据 |
| [模块 API](./b-Office/package/模块API.md) | 跨模块消费合同 |
| [UI 风格与嵌入页面规范](./b-Office/current/HistoryAurora_UI风格与嵌入页面规范.md) | 视觉与嵌入页结构 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-Studio/Module/` | 模块工程与程序集元数据 |
| `b-Code-Studio/Shell/` | 界面源码，按层分目录（见「要点」） |
| `b-Code-Studio/eng/` | 构建与门禁脚本 |
| `b-Code-Verify/` | `Contracts` 合同测试、`ModuleSmoke` 模块装载冒烟 |
| `b-Office/` | 项目文档：`current/` 现行合同、`package/` 消费合同、`history/` 只读归档 |
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

`Shell/` 下只有五个层目录，编号越大越靠上，**依赖只能向下**；目录名即层，命名空间跟着目录走（REQ-UI-074、DEC-029）。

| 目录 | 命名空间 | 内容 |
| --- | --- | --- |
| `0-Neutral/` | `HistoryAurora.Shell.Neutral` | 指令面、日志、纯工具 |
| `1-Base/` | `.Base` | 页面外壳：停靠、顶栏、拖出拖入、页面合并、弹窗 |
| `2-Components/` | `.Components` | 风格令牌、表格、控制面板、泳道、页面描述与渲染器 |
| `3-HostedPages/` | `.HostedPages` | Aurora 自己托管的页面，与模块页同一条路 |
| `4-Composition/` | `.Composition` | 装配根：`ShellWindow` 与内置指令组 |

- 门禁两道：`HistoryAurora.Module.csproj` 按层 `Include`（没分层的目录不参与编译）；`LayerContractTests` 守落点、命名空间与依赖方向。
- 改 `csproj` 时 `Link="%(RecursiveDir)…"` 必须吃掉层目录，否则资源名变成 `2-components/themes/…`：编译零征兆，运行期建窗抛 `IOException`。
- 页面 owner 由指令域推出（`History` + 域名首字母大写），与模块名无关。

## 保留内容
- 本模板项目介绍：此为最初的准备的项目模板
    每个分支项目都会由他去继承
- 作者：Pinavia - 2025

![logo](./Logo.png)
