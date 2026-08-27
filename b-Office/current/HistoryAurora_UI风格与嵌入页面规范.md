# HistoryAurora UI 风格与嵌入页面规范

> 现行视觉合同。2026-08-22 从 HistoryVulcan `b-Office/package/` 迁入本目录：界面令牌、
> 嵌入页结构和顶栏归属属于 HistoryAurora，不再随宿主消费包发布。
>
> 键名已按 DEC-005 写成 `Aurora.*`（无 `Shell.*` 别名）。用法与组件清单见
> [HistoryAurora_组件清单与用法](../package/HistoryAurora_组件清单与用法.md)。
> 业务动作进入宿主 `CommandBus`（见 HistoryVulcan《API 与指令手册》）。

本文是 HistoryAurora 窗口、内置页面和外置 UI 模块的视觉合同。嵌入页面必须复用 Aurora 动态资源，
不得复制固定色板或在页面内维护第二套浅色/深色主题。运行时真值位于
`b-Code-Studio/Shell/Themes/AuroraTokens.xaml`、`AuroraTokens.Dark.xaml` 和 `AuroraControls.xaml`。

## 1. 使用原则

- 页面根容器使用透明背景或 `Aurora.Brush.Surface`，让窗格决定外层背景、圆角和投影。
- 颜色、字体、字号、圆角、间距和控件高度统一使用 `{DynamicResource ...}`；主题切换后必须即时更新。
- 页面不绘制第二层标题栏、窗口边框、窗格卡片或关闭/最大化按钮。页签、拖动和窗口动作由 HistoryAurora 承载。
- 页面区保持工作型界面密度：工具栏紧凑、信息可扫描，不使用营销式大标题、装饰卡片或渐变背景。
- 业务动作进入 `CommandBus`；纯选择、焦点和键盘导航可留在视图层。

## 2. 颜色令牌

| 资源键 | 浅色 | 深色 | 用途 |
| --- | --- | --- | --- |
| `Aurora.Brush.Canvas` | `#F5F6F7` | `#1A1D1C` | HistoryAurora 工作区背景 |
| `Aurora.Brush.Surface` | `#FFFFFF` | `#1D201F` | 页面、窗格和弹层主表面 |
| `Aurora.Brush.SurfaceAlt` | `#F5F6F8` | `#242625` | 次级区域、禁用控件背景 |
| `Aurora.Brush.SurfaceHover` | `#ECEEF1` | `#2A2D2C` | 悬停背景 |
| `Aurora.Brush.SurfacePressed` | `#E0E3E8` | `#343736` | 按下背景 |
| `Aurora.Brush.TextPrimary` | `#1F2328` | `#E2DAC6` | 正文、标题、选中内容 |
| `Aurora.Brush.TextSecondary` | `#6B7280` | `#ACA593` | 说明、元数据、次级图标 |
| `Aurora.Brush.TextDisabled` | `#A1A7B0` | `#77746A` | 禁用文本 |
| `Aurora.Brush.TextOnAccent` | `#FFFFFF` | `#171918` | 主题色实心控件上的文本 |
| `Aurora.Brush.Accent` | `#A87A12` | `#D9A441` | 焦点、选中、主要动作 |
| `Aurora.Brush.AccentHover` | `#8C650E` | `#E8B65C` | 主要动作悬停 |
| `Aurora.Brush.AccentSoft` | `#FAF0D8` | `#33342A` | 选中页签、柔和强调背景 |
| `Aurora.Brush.Danger` | `#C42525` | `#E06C6C` | 错误、破坏性动作 |
| `Aurora.Brush.DangerSoft` | `#FBE9E9` | `#3A2320` | 错误提示背景 |
| `Aurora.Brush.Warning` | `#B26A00` | `#E0A458` | 警告状态 |
| `Aurora.Brush.Success` | `#1E7F4B` | `#5FBE8B` | 成功状态 |
| `Aurora.Brush.Hairline` | `#E4E7EB` | `#2A2D2C` | 必要的内部细分隔线 |
| `Aurora.Brush.ControlBorder` | `#D8DCE2` | `#343736` | 输入控件边框 |
| `Aurora.Brush.WindowButtonHover` | `#DDE1E6` | `#2A2D2C` | 窗口按钮悬停 |
| `Aurora.Brush.WindowButtonPressed` | `#CBD1D8` | `#343736` | 窗口按钮按下 |
| `Aurora.Brush.CloseHover` | `#C42525` | `#C4453D` | 关闭按钮悬停 |
| `Aurora.Brush.CloseHoverPressed` | `#A81E1E` | `#A83A33` | 关闭按钮按下 |

颜色规则：

- `Accent` 只表示选择、焦点和主要动作，不作为大面积页面底色。
- `Hairline` 只用于表格、分段工具条或弹层内部的必要分隔，不用于包围每个区域。
- 错误、警告、成功均使用语义令牌；不要用主题色替代状态色。
- 禁止在业务 XAML 中新增十六进制颜色；确需新增语义时先扩展浅/深两份令牌并补合同测试。

## 3. 字体与文本层级

| 资源键 | 值 | 用途 |
| --- | --- | --- |
| `Aurora.Font.Family` | `Microsoft YaHei UI, Segoe UI` | 中文优先的界面字体 |
| `Aurora.Font.MonoFamily` | `Consolas` | 命令、日志、路径和代码 |
| `Aurora.Font.Body` | `13` | 正文、按钮、输入和表格内容 |
| `Aurora.Font.Small` | `11` | 说明、时间、状态和辅助标签 |
| `Aurora.Font.Mono` | `12` | 控制台与等宽内容 |

优先使用现有文本样式：

- `Aurora.Text.Body`：正文，主文本色，13px。
- `Aurora.Text.Secondary`：辅助正文，次级文本色。
- `Aurora.Text.Caption`：辅助说明，11px。

嵌入页标题通常使用 13px `SemiBold`，紧贴其工具区；不要在紧凑工具页面使用 Hero 级标题。
按钮文本保持常规字重，只有当前选择、分组标题或关键数值使用 `SemiBold`。

## 4. 圆角、间距与固定尺寸

| 资源键 | 值 | 用途 |
| --- | --- | --- |
| `Aurora.Radius.Window` | `0` | 主窗口和独立浮窗外缘 |
| `Aurora.Radius.Inner` | `8` | 页面内部控件、弹层和窗格 |
| `Aurora.Radius.TabTop` | `5,5,0,0` | 顶部页签 |
| `Aurora.Radius.TabInner` | `5` | 页签内部元素 |
| `Aurora.Space.Gap` | `1` | 窗格之间的弱间隔 |
| `Aurora.Space.Pad` | `12` | 页面标准内边距 |
| `Aurora.Space.PadTight` | `8` | 工具栏和紧凑区域内边距 |
| `Aurora.Space.ControlPad` | `10,4` | 文本型控件内容边距 |
| `Aurora.Space.TabStrip` | `3,3,3,0` | 页签相对窗格的内缩 |
| `Aurora.Size.Control` | `28` | 按钮、输入框和选择器最小高度 |
| `Aurora.Size.Tab` | `32` | 页签行、页面顶栏和窗口控制行高度 |
| `Aurora.Size.WindowButton` | `44` | 主窗口控制按钮宽度 |

页面只能有一层 8px 内部圆角，不把卡片嵌套进卡片。固定格式控件应明确 `MinHeight`、网格列宽、
`MinWidth` 或 `MaxWidth`，动态文本必须换行或省略，不能撑动顶栏和工具栏。

## 5. 控件与交互状态

- 普通按钮：表面底色 + `ControlBorder`，高度至少 28px，圆角 8px；悬停/按下使用对应 Surface 令牌。
- 主要按钮：`Accent` 背景、`TextOnAccent` 前景；页面中同一操作组通常只有一个主要按钮。
- 图标按钮：优先使用现有 Lucide 图标，稳定为方形命中区，并提供 ToolTip。
- 输入框、组合框：使用隐式 Aurora 样式；焦点边框使用 `Accent`，禁用状态使用 `TextDisabled`。
- 表格：表头使用 `Aurora.GridHeader`；行选中使用 `AccentSoft`，不要恢复系统默认浅灰模板。
- 菜单、Popup、ToolTip：表面使用 `Surface`，边框使用 `Hairline`，圆角 8px，使用 `Aurora.Shadow.Flyout`。
- 列表、树和 Tab：必须使用 Aurora 模板；深色主题下不得出现系统白底或黑色默认文本。

## 6. 窗口形态：只有工具窗口

自宿主 3.11.3 起，**模块注册的窗口一律是工具窗口**，不再提供文档页（主窗口页面）注册路径。

- 继续按需声明 `DefaultSide = DockSide.Center`：窗口仍然落在中央工作区，与此前的位置一致。
  变化的是**身份**而不是**位置**——它以中央页形态呈现，不再是文档页。
- 无需修改模块代码。已保存的布局在下次启动时自动迁移：存档中的文档页节点会被丢弃并按
  工具窗口重建。
- 唯一的文档页是前端自持的主命令页，模块不能注册、不能替换。

**为什么取消这条路径**：文档页的拖动行为明显弱于工具窗口，并会引出一连串停靠相关缺陷。
中央位置是模块真正需要的东西，文档身份只是当初取得该位置的手段。与其逐个修复文档页的
拖动路径，不如让窗口只有一种形态——两种形态本就不该并存。

## 7. 嵌入页面结构

推荐页面结构：一条紧凑工具栏 + 单一内容区。窗格边距、圆角、投影、页签和顶栏由 HistoryAurora 提供，
模块页面不要重复这些层级。

> **走页面注册协议（`<域>.ui.describe`）的页面不必操心内边距**：1.8.17 起 Aurora 在
> 交给停靠层之前统一包一层 12px（`Aurora.Space.Pad` 的值，REQ-UI-047）。此前描述协议
> 这一路交出的组件树被直接挂进窗格，第一个控件贴着边框画，而描述里没有任何字段能
> 表达内边距——同一个界面里两种页看得出差别，模块作者却改不动。
>
> 下面这段 XAML 只适用于**自带 WPF 控件**那条路（`ui.window` 注解），那条路的内边距
> 仍归页面自己写。

```xaml
<Grid Background="Transparent">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="*" />
    </Grid.RowDefinitions>

    <Grid Margin="{DynamicResource Aurora.Space.PadTight}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <TextBlock Style="{DynamicResource Aurora.Text.Body}"
                   FontWeight="SemiBold"
                   Text="页面标题" />
        <Button Grid.Column="1"
                Command="{Binding RefreshCommand}"
                ToolTip="刷新">
            <TextBlock Text="刷新" />
        </Button>
    </Grid>

    <Grid Grid.Row="1" Margin="{DynamicResource Aurora.Space.Pad}">
        <!-- 页面实际内容 -->
    </Grid>
</Grid>
```

### 顶栏归属

- 主页面顶栏控制整个 HistoryAurora；工具页顶栏控制工具页或独立浮窗。
- 嵌入页不创建自己的窗口最小化、最大化、关闭或拖动区。
- 页面拖出、停靠、专注和恢复由真实页签及 `aurora.ui.*` 命令管线负责。
- 独立浮窗仍只保留一层 HistoryAurora 页面顶栏，模块内容不得根据浮动状态再套标题栏。

### 响应式与可访问性

- 页面在 320px 内容宽度下仍应可操作；工具栏空间不足时换行或收进菜单。
- 文本和背景必须同时检查浅色、深色；不要只验证一种主题。
- 状态不能只靠颜色表达，必须辅以文字、图标或可访问名称。
- 键盘焦点清晰可见；Tab 顺序跟随视觉顺序；纯图标按钮必须有 ToolTip 和可识别名称。

## 8. 验收清单

- 浅色、深色切换后页面无白底、黑字或固定色残留。
- 页面根部没有第二层窗格卡片、标题栏、窗口按钮或重复外边框。
- 正文、辅助文字、等宽内容分别使用 13/11/12px 合同。
- 控件高度至少 28px，顶栏/页签行为 32px，窗口按钮宽度 44px。
- 内部圆角为 8px，页签圆角为 5px，窗口外缘为直角。
- 320px、100%/125%/150% DPI 下无文本遮挡、按钮溢出或水平工具栏截断。
- 所有业务动作可从 `CommandBus` 查询并执行；视图未绕过命令管线直接改变应用状态。
