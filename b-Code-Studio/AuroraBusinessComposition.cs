using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Extensibility.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// HistoryAurora 的模块装配入口。
///
/// 定位：体系对用户可见的那一面——窗口、布局、控制台与主题。
/// DEC-008 起界面不再有独立 exe：它是宿主装载的一个模块，在宿主进程内开窗，
/// 启动与显隐一律走注册的指令，桌面入口由 Mercury 活动坞承担。
///
/// 本类同时是 <see cref="IShellUiProvider"/> 与
/// <see cref="IShellCommandWorkbenchProvider"/>：界面既然整体成了模块，
/// 宿主自己就不再持有任何界面实现，其余模块要用的注册器与命令工作台挂载点
/// 都只能由这里交回去。
/// </summary>
public sealed class AuroraBusinessComposition
    : IModuleContextAware, IShellUiProvider, IShellCommandWorkbenchProvider, IUiModule
{
    private IModuleContext? _context;

    /// <summary>界面就绪等待上限；超时只告警不失败，宿主下一轮重载会补上。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 宿主在装载时注入上下文，提供权威命令总线、设置、日志与数据根目录。
    ///
    /// 界面在这里启动而不是在 <c>IUiModule.CreateUi</c> 里：CreateUi 由宿主在**取到注册器
    /// 之后**才调用，而注册器正是界面创建出来的——放那儿会死锁在自己身上。
    /// </summary>
    public void Attach(IModuleContext context)
    {
        _context = context;
        AuroraShellHost.EnsureStarted(context, ReadyTimeout);
    }

    /// <summary>供宿主转交给其余 UI 模块的界面注册器；界面未就绪时为 null。</summary>
    public IShellUiRegistrar? ShellUi => AuroraShellHost.Registrar;

    /// <summary>
    /// 供宿主转交给命令工作台模块（HistoryMercury）的挂载点；界面未就绪时为 null。
    ///
    /// 主窗口本身实现 <see cref="IShellCommandWorkbenchHost"/>——工作台要的
    /// 共享选中态、控制台目录会话与补全路由都长在它身上，别处给不出来。
    /// 少了这条通道，命令集与指令详情两页不会出现，控制台补全同时退化到本地兜底。
    /// </summary>
    public IShellCommandWorkbenchHost? CommandWorkbench => AuroraShellHost.Window;

    /// <summary>
    /// 空实现。本模块**承载**界面而不是往界面里加东西，窗口的生死由
    /// <see cref="AuroraShellHost"/> 的 STA 线程自己管。
    ///
    /// 之所以还要实现 <see cref="IUiModule"/>：宿主只把实现了它的模块放进 UiModules，
    /// 而 <c>CreateUi</c> 正是从那个集合里找 <see cref="IShellUiProvider"/>。
    /// 不实现就永远轮不到被问——症状是界面起来了、四个模块的页面却一个都没有，
    /// 日志里也没有任何错误。
    /// </summary>
    public void CreateUi()
    {
    }

    /// <summary>空实现。钉住模块不会被卸载，界面随宿主进程一起结束。</summary>
    public void DestroyUi()
    {
    }

    /// <summary>当前生效的宿主上下文；未装载时为 null。</summary>
    internal IModuleContext? Context => _context;

    /// <summary>
    /// 迁入命令时统一走这里登记。
    ///
    /// 宿主给的是一个与实时注册表隔离的暂存注册表，由宿主随模块一起提交与回收——
    /// 因此模块不得自己持有实时注册表引用，否则卸载时无法完整回收。
    /// </summary>
    internal void RegisterCommands(Action<CommandRegistry> configure)
        => _context?.RegisterCommands(configure);
}
