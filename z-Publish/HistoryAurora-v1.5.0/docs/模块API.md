# HistoryAurora 模块 API

适用版本：**1.5.0**（宿主 HistoryVulcan **5.0.0**）

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

未装本模块时 `vulcan.ui.*` 是未知指令；布局操作是 `aurora.ui.*`。

## 宿主要求

最低宿主版本 **HistoryVulcan 5.0.0**，由 `b-Code-Studio/AuroraVersion.props` 的
`MinimumHistoryVulcanVersion` 单点声明。
