# HistoryAurora

> 宿主前端服务：浏览器里的场景、布局、控制台与主题

宿主装载 Aurora 后，在浏览器打开 `http://127.0.0.1:5280`（或执行 `vulcan.app.show`，没有打开的网页时会自动打开系统浏览器）。
本模块没有桌面窗口：界面就是这个网页（以后手机与 app 壳也访问同一个服务）；宿主里的模块要弹窗、刷新、切场景时，指令推到最近活跃的网页执行。
开发时也可以单独 `dotnet run --project b-Code-Web/HistoryAurora.Web.csproj` 起网页（不经模块，宿主推送不到它）。
网页能力（场景与布局、表格、面板、浮窗、控制台、弹窗与文件选择……）与验证入口见 `b-Office/现行约定.md` 的 Web 条目。
当前只监听本机，公网与手机接入由后续 Portunus 工作承担。

## 定位

HistoryAurora 是 HistoryVulcan 的前端模块，经 `RegisterFrontend` 登记为宿主唯一前端：场景、布局、
控制台、命令集、弹窗与页面宿主都在这里，界面由它拉起的 Aurora Web 送到浏览器。名字取自罗马黎明女神——它是体系对用户可见的那一面。

- 不是独立应用：Web 子进程随模块装载启动、随卸载退出，发布只有模块包。没装 Aurora 的宿主没有界面。
- 别的模块不自建控件，而是以描述化页面协议注册页面，由 Aurora 的组件层渲染。
- 它的由来是把宿主前端进程自持的 39 条界面命令搬出宿主，让宿主只剩指令总线（MCP 工具一个不少）。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-026` |
| 角色 | 宿主模块（`kind=module`），宿主唯一前端 |
| 指令域 | `aurora` |
| 界面 | 浏览器访问 `http://127.0.0.1:5280`（Aurora Web，模块子进程） |
| MCP 投影 | `readonly` |
| 版本与宿主下限 | [`AuroraVersion.props`](./b-Code-Studio/AuroraVersion.props) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `ui` | `show` / `dock` / `float` / `layouts` / `panels` / `dialog` / `selectfile` … | 页面、布局、面板、弹窗与文件选择（在网页里执行） |
| `component` | `list` / `show` | 页面协议与组件用法的自描述 |
| `scene` | `list` / `open` / `go` / `save` / `reset` / `delete` | 场景 |
| `log` | `source` / `level` / `keyword` / `clear` / `export` … | 控制台筛选与导出 |
| `command` | `history` / `copyexample` / `runreadonly` | 命令集 |
| `app` | `about` / `theme` / `opendata` | 关于、主题与数据目录 |
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
| `b-Code-Shell/AuroraShell/` | 壳：一套 MAUI 代码出电脑 exe 与安卓 apk，只装一个网页视图（不引用 Aurora，几乎不用更新） |
| `b-Code-Web/` | Aurora Web：网页与服务（模块的子进程） |
| `b-Code-Studio/Shell/` | 模块与 Web 共用的纯 C# 层，按层分目录（见「要点」） |
| `b-Code-Studio/eng/` | 构建与门禁脚本 |
| `b-Code-Verify/` | `Contracts` 合同测试、`ModuleSmoke` 模块装载冒烟、`Web` 网页测试 |
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

Aurora 是装在宿主上的**无界面服务**：模块拉起 `b-Code-Web`（Aurora Web）子进程，界面只在浏览器里（默认 `http://127.0.0.1:5280/`）。
宿主里的模块调 `aurora.*` 界面指令时，模块经标准输入输出转给 Web，Web 经 SSE 推给最近活跃的网页执行。

| 目录 | 内容 |
| --- | --- |
| `b-Code-Web/` | Aurora Web：ASP.NET Core 服务 + `wwwroot/` 网页（随程序集内嵌）；推送通道与父进程链路 |
| `b-Code-Studio/Module/` | 模块：拉起 Web、登记前端、`aurora.*` 指令（转给网页的清单 `AuroraCommands.json` + 模块自持的取数） |
| `b-Code-Shell/AuroraShell/` | 壳：一套 MAUI 代码出电脑 exe 与安卓 apk，只装一个网页视图（不引用 Aurora，几乎不用更新） |
| `b-Code-Studio/Shell/` | 纯 C# 共享层 `0-Neutral` / `2-Components` / `3-HostedPages`，依赖只能向下；Web 直接链接其中的描述解析与泳道布局 |

- 加一条界面指令：网页 `browser-commands.js` 实现 + `AuroraCommands.json` 加一条自描述；`HeadlessServiceContractTests` 对账。
- 门禁两道：`HistoryAurora.Module.csproj` 按层 `Include`（没分层的目录不参与编译）；`LayerContractTests` 守落点、命名空间与依赖方向。
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
