# HistoryAurora 组件清单与用法

> 面向模块作者。本文列出 Aurora 前端提供的样式键与页面注册方式，以及**怎么用**。
> 颜色、间距、嵌入页结构和顶栏归属见
> [HistoryAurora_UI风格与嵌入页面规范](../current/HistoryAurora_UI风格与嵌入页面规范.md)。
> 组件的建设计划与调研依据见 `../history/前端组件计划表V1.0.md`；
> 长期约束见 `../current/技术合同.md`。

## 一分钟上手

模块不自己画界面骨架，只做两件事：**注册一个工具窗口**，**在页面里引用现成样式键**。

```csharp
// 1) 实现 IUiModule + IShellUiAware，在 CreateUi 里注册
public void CreateUi()
{
    _registration = _shellUi.RegisterToolWindow(new ToolWindowDescriptor
    {
        Id = "myview",                    // 模块内唯一
        Title = "我的页面",
        DefaultSide = DockSide.Center,    // Center / Left / Right / Bottom / Tab
        IsSingleton = true,
        ContentFactory = () => new MyView(busAccessor),
    }, "HistoryMyModule");                // owner，必须是模块名
}

public void DestroyUi() => _registration?.Dispose();
```

```xml
<!-- 2) 页面里直接引用样式键，不要自己定义颜色 -->
<Button Style="{DynamicResource Aurora.Button.Accent}" Content="执行" />
<TextBlock Foreground="{DynamicResource Aurora.Brush.TextSecondary}" Text="说明文字" />
```

**一律用 `DynamicResource`，不要用 `StaticResource`。** 主题切换（浅色/深色）靠动态查找生效，
用静态引用的控件在切主题后颜色不会更新。

## `ToolWindowDescriptor` 字段

| 字段 | 说明 |
|---|---|
| `Id` | 窗口标识，模块内唯一。宿主会加 owner 前缀，不必自己拼模块名 |
| `Title` | 标签页显示名 |
| `DefaultSide` | `Center` 中央工作区 / `Left` / `Right` / `Bottom` / `Tab` 并入标签组 |
| `DefaultRatio` | 侧边停靠时占主窗体比例，如 `0.38` |
| `DefaultTabTarget` | `DefaultSide = Tab` 时的目标窗口 Id |
| `IsSingleton` | 是否只允许一个实例 |
| `ContentFactory` | 返回页面实例的工厂，延迟到真正显示时才调用 |

**停靠位选择的一条经验**（来自 Janus 的实测教训）：`DefaultTabTarget` 指向**别的模块**提供的
窗口是不稳的——模块装载顺序不保证目标先到，先到就并入、没到就回退侧边停靠，
首次启动落位因此随机。要并标签组，就并到宿主自己注册的窗口（如控制台）；
不确定时用 `DockSide.Center`，它不依赖任何模块。

## 设计令牌（65 个）

只列最常用的。完整清单见 `Themes/AuroraTokens.xaml`。

### 颜色 `Aurora.Brush.*`

| 键 | 用途 |
|---|---|
| `Surface` / `SurfaceAlt` / `SurfaceHover` | 背景、次级背景、悬停态 |
| `TextPrimary` / `TextSecondary` / `TextDisabled` | 正文 / 辅助 / 禁用 |
| `Accent` / `AccentSoft` | 强调色、强调色浅底 |
| `ControlBorder` / `Hairline` | 控件描边、分隔细线 |

### 字体 `Aurora.Font.*`

`Family` 正文字体族、`MonoFamily` 等宽族、`Body` 正文号、`Small` 小号、`Mono` 等宽号。

命令文本、路径、哈希一律用 `Mono` / `MonoFamily`——变宽字体下对不齐，列表里尤其明显。

### 尺寸与间距

`Aurora.Space.ControlPad` 控件内边距、`Aurora.Size.Control` 标准控件高、`Aurora.Size.Tab` 标签高、
`Aurora.Radius.Inner` 内圆角、`Aurora.Shadow.Flyout` 浮层阴影。

## 具名样式（16 个）

### 按钮 `Aurora.Button.*`

| 键 | 何时用 |
|---|---|
| `Base` | 普通操作 |
| `Accent` | 页面主操作，一页**至多一个** |
| `Danger` | 删除、重置等不可逆操作 |
| `Ghost` | 工具条里的次要操作，无边框 |

### 分段条 `Aurora.Segment.*`

用于把一排相关控件收进一条带底的横条，视觉上成为一个整体。

| 键 | 目标类型 |
|---|---|
| `Bar` | Border——外层容器，先放它 |
| `Label` / `TextBox` / `ComboBox` / `Button` / `CheckBox` | 条内各类控件 |
| `Divider` | 条内分隔 |

```xml
<Border Style="{DynamicResource Aurora.Segment.Bar}">
  <StackPanel Orientation="Horizontal">
    <TextBlock Style="{DynamicResource Aurora.Segment.Label}" Text="项目" />
    <ComboBox Style="{DynamicResource Aurora.Segment.ComboBox}" />
    <Border Style="{DynamicResource Aurora.Segment.Divider}" />
    <Button Style="{DynamicResource Aurora.Segment.Button}" Content="刷新" />
  </StackPanel>
</Border>
```

> **已知缺件，正在补**：该家族目前没有 RadioButton / Toggle 成员，
> 即「互斥分段切换」（点一个亮一个的页面切换器）暂时无对应组件。Janus 的
> `OperationSegment` 就是为此自造的 40 行 ControlTemplate。
>
> `Aurora.Segment.Toggle` 是组件计划的 **P0**，且是「禁止自建组件」这条规矩的**解禁前置**——
> 它落地之前不会对模块执行该禁令。落地后请删除自造版本，外观不会变。

### 列表与表格

| 键 | 目标类型 | 说明 |
|---|---|---|
| `Aurora.GridHeader` | GridViewColumnHeader | 表头 |
| `Aurora.Item.Base` | ListViewItem | 行样式，含选中/悬停态 |

**表格用 `ListView` + `GridView`，不要用 `DataGrid`。** 现有模块 12 个页面里 ListView 用了 21 次、
GridViewColumn 46 次，而 DataGrid 只有 2 次——`DataGrid` 不在主题化范围内，用了会和其余页面
外观脱节。

### 其他

`Aurora.ComboBox.ToggleButton`、`Aurora.TreeExpander`、`Aurora.ScrollBar.Thumb`——
一般不必直接引用，对应控件套用后自动生效。

## 弹窗 `aurora.ui.dialog`（1.2.0）

模块不要自己 `new Window`。前端独立之后，顶层窗口拿不到 `Aurora.*` 令牌，
`DynamicResource` 会静默退化成系统外观。弹窗由 Aurora 自持，自己合并主题字典，
并用与主窗体相同的自绘顶栏（无系统标题栏）。

写操作的宿主 `ConfirmPrompt` 在进程内也走同一组件：Aurora 启动时接管宿主确认通道。

```text
aurora.ui.dialog kind=message title=关于 body="……"
aurora.ui.dialog kind=confirm title=需要确认 body="覆盖现有文件？" danger=true defaultcancel=true
aurora.ui.dialog kind=prompt title=生成恢复提交 body="恢复提交说明" value="revert abc"
aurora.ui.dialog kind=content title=预览 body="摘要" content="<大段正文>"
```

| kind | 用途 | 结果 |
|---|---|---|
| `message` | 一段说明 + 关闭 | 点关闭即成功 |
| `confirm` | 确认 / 取消；`timeout=` 秒后拒绝；`danger=true` 主按钮用危险档 | 取消或超时返回失败 |
| `prompt` | 带输入的确认 | 成功时 `Message`/`Data` 是输入文本 |
| `content` | 大段只读等宽正文（历史预览） | 点关闭即成功 |

模块不要再叠一层确认框。

**不要**把弹窗写进 `<域>.ui.describe` 的页面树。它不是停靠页，页面渲染器里没有
`dialog` 组件——写了只会变成显式占位。

## 规矩

1. **不自建组件。** 模块 XAML 里**不得出现 `Style x:Key=` 与 `<ControlTemplate>`**。
   需要的成员不存在时，向 Aurora 提缺件，不要在自己仓里造一个——自造版本不跟随主题演进，
   最终表现为「只有这一页长得不一样」。实测三个模块的全部自造（2 样式 + 4 模板）
   都源自同一个缺件，补齐家族即可全部消除。
2. **不写字面颜色。** 页面里出现 `#RRGGBB` 或 `Colors.*` 即为违规——深色主题下必然出错。
3. **一律 `DynamicResource`。** 见开头说明。
4. **业务入口走命令总线。** 按钮点击应调 `CommandBus`，不要直接改服务状态或窗口状态。
   这样同一能力在控制台、脚本、MCP 里都能用，不会变成只有点得到的「断头指令」。
5. **不要依赖别的模块的窗口存在。** 见上文 `DefaultTabTarget` 的经验。

## 破坏性变更：`Shell.*` → `Aurora.*`（模块必须适配）

前端从 HistoryVulcan 切到 Aurora 时，**65 个令牌与 16 个具名样式的键全部改名**：
`Shell.Brush.Accent` → `Aurora.Brush.Accent`，依此类推。家族结构与语义不变，只换前缀。

**不提供 `Shell.*` 别名，没有弃用期。** 别名意味着每个组件维护两套键、每次演进同步两处，
且无法判断某个模块实际用的是哪一套；而"弃用期"在实践中等于永久保留。一次性断掉，
适配才有明确的完成标志。（依据：Aurora DEC-005）

### 这个变更不会让你的模块崩溃——这正是危险之处

`DynamicResource` 找不到键时 WPF **不抛异常**，只是不套用该值。所以未适配的页面
**不会报错，而是静默退化成 WPF 默认外观**——灰按钮、系统字体、和其余页面明显不一致。

不要指望运行时告诉你哪里漏了。用检查器：

```bash
# 残留旧键
grep -rn "Shell\.[A-Za-z.]*" <你的模块仓> --include=*.xaml

# 自建组件（本轮起禁止，见下）
grep -rn 'Style x:Key=\|<ControlTemplate' <你的模块仓> --include=*.xaml

# 字面颜色
grep -rnE '#[0-9A-Fa-f]{6}|Colors\.' <你的模块仓> --include=*.xaml
```

三条都返回空，才算适配完成。

### 适配步骤

1. 全仓替换 `Shell.` → `Aurora.`（键名前缀，注意别误伤 `ShellWindow` 等类型名）；
2. 删除自建样式与模板，改用组件库对应成员；
3. 跑上面三条检查；
4. 浅色与深色各目视一次——静默退化不会报错，只能看。
