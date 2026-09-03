using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Components.Pages;

/// <summary>
/// 渲染完的一页。<paramref name="Root"/> 为 null 表示这一页渲染失败——
/// 失败原因已经记进日志，调用方只需跳过它。
/// </summary>
internal sealed record PageContent(FrameworkElement? Root, IReadOnlyList<string> Missing);

/// <summary>一页注册完的结果。缺件被带出来而不是丢掉，供缺件清单与组件申请台账消费。</summary>
internal sealed record PageRegistration(bool Registered, IReadOnlyList<string> Missing);

/// <summary>
/// 描述 → 界面上的一页。**全仓只有这一条路**（REQ-UI-051）。
///
/// 1.8.18 之前有两条：模块页走 <see cref="ModulePageLoader"/>，由它包一层 12px；
/// 而 Aurora 自持的四页各自手写 <c>ToolWindowDescriptor</c>，内边距各写各的——
/// 组件测试页写 12，命令集 / 指令详情 / 模块管理写 8。**同一个界面里，
/// Aurora 自己的页比模块的页窄 4px**，用户一眼看出来了，而代码里没有任何一处
/// 「说了算」的地方可以改它，因为根本不存在那样一处。
///
/// 两条通道的真正代价不是那 4px，是**每加一条页面级规则都要写两遍**：
/// 这一版要求「工具页不滚，只有组件内部滚」（REQ-UI-050），若还是两条通道，
/// 就得在拉取器和四个视图类里各写一遍，然后等着其中一处被漏掉。
///
/// 因此本类是页面与停靠层之间的**唯一**接缝：渲染归 <see cref="PageRenderer"/>，
/// 停靠归 <see cref="IDockingService"/>，而「一页相对窗格长什么样」只归这里。
/// </summary>
internal sealed class PageRegistrar(
    CommandBus bus,
    IShellLog log,
    // 停靠层。**自持页那一路传 null**：它在停靠层建出来之前就要把内容备好
    // （命令集是中央主文档，DockingHost.Initialize 时就得在场），而它本来也不调
    // Register——注册走 TakeOverDescriptor。为 null 时 Register 记一条 Error 并拒绝，
    // 不静默地不建页。
    IDockingService? docking = null,
    HistoryAurora.Shell.Components.Actions.ActionRegistry? actions = null,
    HistoryAurora.Shell.Neutral.CommandSurface.AuroraCompletionProvider? completions = null,
    HistoryAurora.Shell.Components.Selection.SelectionChannels? channels = null,
    PageDataRefresher? refresher = null,
    HistoryAurora.Shell.Components.Table.IColumnOrderStore? columnOrder = null)
{
    private const string Source = "page";

    /// <summary>
    /// 页面内容相对窗格的内边距（REQ-UI-047）。
    ///
    /// **这一层归 Aurora，不归页面作者。** 描述协议里没有任何字段能表达它，
    /// 这是刻意的：内边距是页面与窗格之间的关系，不属于页面里的任何一个组件，
    /// 让模块各自声明只会得到一堆互不相同的值。
    /// </summary>
    private const double PagePad = 12;

    /// <summary>
    /// <c>Aurora.Space.Pad</c> 的值，**不走 DynamicResource**。
    ///
    /// 间距令牌在浅色与深色里取值相同（都是 12），不随主题变化；而要让
    /// <c>SetResourceReference</c> 在这一层解析得到，就得把主题字典并进这个 Border——
    /// 那会把整棵页面钉在被并进来的那一套配色上，主题一切换它不跟。
    /// 一个不随主题变的数字，不值得用一条会破坏主题跟随的机制去取。
    /// </summary>
    public static FrameworkElement Inset(FrameworkElement content)
        => new Border
        {
            Child = content,
            Padding = new Thickness(PagePad),

            // 工具页不滚（REQ-UI-050）。装不下时**裁掉**，不是长出去也不是长出滚动条：
            // 页面级滚动一旦存在，鼠标停在控制面板上滚不动、移开一点又能滚，
            // 同一块版面出现两套滚动语义。滚动只发生在组件内部——表格、泳道、
            // 控制台各自有视口，它们被 PageRenderer 的星号行限住高度才滚得起来。
            //
            // 裁切是有意让「装不下」变成看得见的事故：它会被当场发现并去改版面，
            // 而滚动条会让一个排版错误一直活着。
            ClipToBounds = true,
        };

    /// <summary>
    /// 描述 → 可挂进窗格的内容。**这是「一条通道」真正指的那一段**：渲染、包边、裁切。
    ///
    /// 注册那一步刻意留在外面，因为两类页面的注册路径按契约就不同：模块页走
    /// <see cref="Register"/>（<c>RegisterWindow</c> + 按 owner 回收），而 Aurora 自持的
    /// 四页走 <c>TakeOverDescriptor</c>——那条路要保留「派生应用按 Id 声明 ToolWindows、
    /// 界面按 Id 合并」这个公开契约，绕过去会让派生应用声明的位置与标题静默失效。
    ///
    /// 分歧只到这里为止：**长什么样**由本方法一处说了算，两边不可能再长出 8 与 12 之差。
    ///
    /// 渲染失败只影响这一页，不牵连同一来源的其他页，更不牵连别的来源（协议 §1.5）。
    /// </summary>
    public PageContent Build(string owner, PageDescription page)
    {
        try
        {
            var rendered = PageRenderer.Render(
                page,
                new PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = owner,
                    Actions = actions,
                    Completions = completions,
                    Channels = channels,
                    Refresher = refresher,
                    ColumnOrder = columnOrder,
                });

            return new PageContent(Inset(rendered.Root), rendered.MissingComponents);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, Source, $"{owner}: 页面 {page.Id} 渲染失败: {ex.Message}");
            return new PageContent(null, []);
        }
    }

    /// <summary>建一页并挂上停靠层（模块页这一路）。</summary>
    public PageRegistration Register(string owner, PageDescription page)
    {
        if (docking == null)
        {
            log.Log(ShellLogLevel.Error, Source,
                $"{owner}: 页面 {page.Id} 无法注册——本注册器没有停靠层。"
                + "自持页应走 TakeOverDescriptor，不该调到这里。");
            return new PageRegistration(false, []);
        }

        var built = Build(owner, page);
        if (built.Root is not { } content)
            return new PageRegistration(false, built.Missing);

        try
        {
            docking.RegisterWindow(new ToolWindowDescriptor
            {
                Id = page.Id,
                Title = page.Title,
                DefaultSide = ParseSide(page.Placement.Side),
                DefaultRatio = Clamp(page.Placement.Ratio),
                DefaultTabTarget = page.Placement.TabTarget,
                DefaultVisible = page.Placement.Visible,
                IsSingleton = page.Placement.Singleton,
                ContentFactory = () => content,
            }, owner);
        }
        catch (Exception ex)
        {
            log.Log(ShellLogLevel.Warn, Source, $"{owner}: 页面 {page.Id} 注册失败: {ex.Message}");
            return new PageRegistration(false, built.Missing);
        }

        return new PageRegistration(true, built.Missing);
    }

    public static DockSide ParseSide(string? side)
        => (side ?? "").ToLowerInvariant() switch
        {
            "left" => DockSide.Left,
            "top" => DockSide.Top,
            "bottom" => DockSide.Bottom,
            "center" => DockSide.Center,
            "tab" => DockSide.Tab,
            _ => DockSide.Right,
        };

    /// <summary>比例必须严格落在 (0,1)，越界按缺省值处理而不是让停靠库抛。</summary>
    public static double Clamp(double ratio)
        => ratio is > 0 and < 1 ? ratio : 0.25;
}
