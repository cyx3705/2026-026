# HistoryAurora 1.15.0

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

当前版本 **1.15.0**。Aurora 已承载宿主前端的窗口、布局、控制台与主题能力；新增隐藏只读
`aurora.log.snapshot`，从控制台实际使用的内存日志缓冲区提供有界结构化快照，供
HistoryDiana 的 `diana.log.read` 在当前进程命令总线上调用。日志进入缓冲前统一净化
账号、密码、令牌、密钥和凭据 URI 等敏感文本，控制台与 AI 查询共享同一份净化结果。

## 目录

| 路径 | 用途 |
|---|---|
| `b-Code-Studio/` | 模块源码。`Module/` 是工程，`Views/` 放界面，`eng/` 放构建与门禁脚本 |
| `b-Code-Verify/` | `Contracts` 合同测试、`Smoke` 冒烟、`ModuleSmoke` 模块装载冒烟 |
| `b-Office/current/` | 现行合同：项目概览、技术合同、有效决策、验证合同 |
| `b-Office/package/` | 对外消费合同编辑源 |
| `z-Publish/` | 根部为当前候选，`history/` 为不可变发布归档。**纳入 git，排除规则不得触碰** |

## 构建

宿主快照按相对路径解析（假定本仓与 `2026-023-HistoryVulcan` 在同一库根下）。AI 工作树落在
库根之外时，由 `diana.worktree.create` 生成的 `Directory.Build.user.props` 把
`HistoryVulcanPackageRoot` 指回来源仓绝对路径；该文件不入库。

```bash
dotnet restore .\HistoryAurora.sln --locked-mode -p:NuGetAudit=false
```

其余命令见 `project.manifest.json` 的 `commands` 段。
