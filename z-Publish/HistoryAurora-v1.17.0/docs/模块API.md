# HistoryAurora 模块 API

适用版本：**1.17.0**（宿主 HistoryVulcan **5.1.0**）

## 当前对外面

HistoryAurora 是宿主装载的界面模块。5.0 起入口只实现 `IModuleContextAware`：
`Attach` 里启动窗口，卸载走 `IDisposable`。不再实现已删除的 `IUiModule` /
`IShellUiProvider` / `IShellUiRegistrar`。

界面命令以 `aurora.*` 登记（来源 `framework:frontend`）。宿主生命周期壳命令
`vulcan.app.show` / `hide` / `close` / `focusconsole` 仍由宿主注册，执行体经
`CommandBus.FrontendExecutor` 转到本模块。

其它模块要露出窗格时，登记带注解的命令，活对象放 `CommandResult.Data`：

```text
ui.window = 窗格 id
ui.side   = left | right | top | bottom | center | tab
ui.title  = 标题（可省，回退命令摘要）
ui.ratio  = (0,1) 比例（可省，默认 0.25）
```

Aurora 在界面空闲时扫描注册表、执行该命令，并把返回的 `UIElement` 停靠进布局。
页面描述协议 V1（`<域>.ui.describe`）仍然可用，走 `aurora.ui.reloadpages`。

1.17.0 起 Aurora 登记 `aurora.host.ready`：宿主（5.1.3 起）装完全部模块后按命令名通知，
Aurora 在那时做整轮重拉。**对其它模块的影响是好的一面**——你的 `<域>.ui.describe`
从此一定在「全部模块都接上」之后才被问到，不必再为「启动时 Aurora 问得太早」写重试或
在 `Attach` 里抢着 `aurora.ui.invalidate`。这条命令是宿主的生命周期回调，
不由人或远端触发，模块也不要直接调它。

1.13.0 为页面协议增加三项兼容能力：表格列 `cellAction`、`aurora.ui.dialog kind=choice`
与面板按钮 `icon=refresh-cw`。旧页面和旧面板声明无需修改。

- `cellAction` 绑定动作 id，动作占位符默认读取被点行同名字段；空单元格不触发，断链会显示诊断。
- `kind=choice` 的 `options` 是非空 `{label,value}` JSON 数组，成功结果的 `Message`/`Data`
  为所选 value。
- `icon` 只属于面板按钮，图形由 Aurora 管控；当前唯一值为 `refresh-cw`，未知值按合同拒绝。

未装本模块时 `vulcan.ui.*` 是未知指令；布局操作是 `aurora.ui.*`。

## 宿主要求

最低宿主版本 **HistoryVulcan 5.1.0**，由 `b-Code-Studio/AuroraVersion.props` 的
`MinimumHistoryVulcanVersion` 单点声明。
