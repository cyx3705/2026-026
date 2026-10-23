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
