# HistoryAurora 1.20.4

OneHistory 体系的 **宿主前端界面模块**。名字取自罗马黎明女神 Aurora——本模块是宿主对用户
可见的那一面：窗口、布局、控制台与主题。

本仓库是 HistoryVulcan 的界面模块，不是独立应用。宿主合同以
`../2026-023-HistoryVulcan/b-Office/package/` 为准。

## 存在的理由

HistoryVulcan 的目标是「只保留指令总线与 MCP，越轻越好」。3.13.0 实测其 `vulcan` 域
81 条命令中：

| 注册方 | 条数 | 构成 | 投影为 MCP 工具 |
|---|---|---|---|
| `frontend:HistoryVulcan.Frontend` | **39** | ui 21、log 10、app 4、command 4 | **0** |
| `framework:service` | 42 | mcp 12、prompt 8、app 7、module 7、command 4、svc 4 | 24 |

那 39 条占着服务进程注册表近一半条目，参与每次 schema 导出与目录快照，却对 MCP 面零贡献。
HistoryAurora 的任务就是把它们从宿主搬出来——搬走之后 MCP 工具一个不少。

## 现状

当前版本 **1.20.4**。表格统一表头底板、虚拟化回收与增量更新接口，刷新保留原内容并常驻显示状态、数量、更新时间（REQ-UI-108 ～ 110）。

1.17.0：启动期的界面发现不再靠「注册表安静 150ms」这个猜测：
登记 `aurora.host.ready`，宿主（5.1.3 起）装完全部模块后按命令名通知，界面在钩子里
做整轮重拉，防抖定时器降为兜底（DEC-030 / REQ-UI-075）。本版不抬宿主下限，
在 5.1.2 及以前那条命令永远不会被调用，行为与 1.16.0 一致。

1.16.0：界面的三层（外加中立层与装配根）从「测试里的一张字典」收回到目录本身：
`Shell/` 下只有五个层目录，命名空间跟着目录走，没分层的目录不参与编译。收目录的同时
暴露并修掉了三处此前门禁看不见的反向依赖（DEC-029），界面行为零变化。

Aurora 已承载宿主前端的窗口、布局、控制台与主题能力；1.14.0 起有隐藏只读
`aurora.log.snapshot`，从控制台实际使用的内存日志缓冲区提供有界结构化快照，供
HistoryDiana 的 `diana.log.read` 在当前进程命令总线上调用。日志进入缓冲前统一净化
账号、密码、令牌、密钥和凭据 URI 等敏感文本，控制台与 AI 查询共享同一份净化结果。

## 目录

| 路径 | 用途 |
|---|---|
| `b-Code-Studio/Module/` | 模块工程与程序集元数据 |
| `b-Code-Studio/Shell/` | 界面源码，**按层分目录**，见下 |
| `b-Code-Studio/eng/` | 构建与门禁脚本 |
| `b-Code-Verify/` | `Contracts` 合同测试、`Smoke` 冒烟、`ModuleSmoke` 模块装载冒烟 |
| `b-Office/current/` | 现行合同：项目概览、技术合同、有效决策、验证合同 |
| `b-Office/package/` | 对外消费合同编辑源 |
| `z-Publish/` | 根部为当前候选，`history/` 为不可变发布归档。**纳入 git，排除规则不得触碰** |

## 分层

`Shell/` 下只有五个层目录，编号越大越靠上，**依赖只能向下**。目录名即层，
命名空间跟着目录走，根部不放任何散文件（REQ-UI-074、DEC-029）。

| 目录 | 命名空间 | 装的是什么 |
|---|---|---|
| `0-Neutral/` | `HistoryAurora.Shell.Neutral` | 指令面、日志、纯工具。不认识任何一层界面 |
| `1-Base/` | `.Base` | 页面外壳：停靠、顶栏、浮窗、拖出拖入、页面合并、弹窗。**不知道页面里画的是什么** |
| `2-Components/` | `.Components` | 演进层：风格令牌、表格、控制面板、泳道、页面描述与渲染器。别的模块注册页面就是在消费这一层 |
| `3-HostedPages/` | `.HostedPages` | Aurora 自己托管的那几页。与模块页同一条路，没有特殊待遇 |
| `4-Composition/` | `.Composition` | 装配根：`ShellWindow` 与内置指令组。没有任何东西可以依赖它 |

门禁有两道：`HistoryAurora.Module.csproj` 一层一组 `Include`——没分层的顶层目录不参与编译；
`LayerContractTests` 四条守落点、命名空间对齐与依赖方向。

> 改 `csproj` 时注意 `Link="%(RecursiveDir)…"` 必须把层目录吃掉。写回
> `..\Shell\**\*.xaml` 会让资源名变成 `2-components/themes/…`，而 pack URI 找的是
> `themes/…`：编译期零征兆，运行期建窗时抛 `IOException`。

## 构建

宿主快照按相对路径解析（假定本仓与 `2026-023-HistoryVulcan` 在同一库根下）。AI 工作树落在
库根之外时，由 `diana.worktree.create` 生成的 `Directory.Build.user.props` 把
`HistoryVulcanPackageRoot` 指回来源仓绝对路径；该文件不入库。

```bash
dotnet restore .\HistoryAurora.sln --locked-mode -p:NuGetAudit=false
```

其余命令见 `project.manifest.json` 的 `commands` 段。
