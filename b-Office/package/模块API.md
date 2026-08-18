# HistoryAurora 模块 API

适用版本：**0.1.0**（骨架阶段）

## 当前对外面

**无。** 本模块尚未注册任何命令，也未暴露任何 MCP 工具。

`AuroraBusinessComposition` 实现 `IModuleContextAware`，装载时取得 `IModuleContext` 并写一条
装载日志，仅此而已。

## 计划中的对外面

承接 HistoryVulcan 前端进程当前自持的 39 条命令。**迁入不改命令名**——消费方看到的
`vulcan.ui.*` / `vulcan.log.*` / `vulcan.app.*` / `vulcan.command.*` 名称保持不变，
变的只是注册方（`frontend:HistoryVulcan.Frontend` → 本模块）。

| 组 | 条数 | 迁入状态 |
|---|---|---|
| `vulcan.log.*` | 10 | 未开始 |
| `vulcan.ui.*` | 21 | 未开始 |
| `vulcan.app.*`（前端侧） | 4 | 未开始 |
| `vulcan.command.*`（前端侧） | 4 | 未开始 |

这 39 条在宿主中 `PolicyVisible` 全为 false，本就零条投影为 MCP 工具；迁入后该性质不变，
`mcpExposure` 保持 `readonly`。

## 宿主要求

最低宿主版本 **HistoryVulcan 3.13.0**，由 `b-Code-Studio/AuroraVersion.props` 的
`MinimumHistoryVulcanVersion` 单点声明。

3.13.0 起宿主 Web 网关要求一次性 IPC 凭据（宿主 DEC-046），低于该版本的宿主不在支持范围。
