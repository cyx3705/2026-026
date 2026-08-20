using System.Windows;
using System.Windows.Threading;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Services;
using HistoryAurora.Shell;


namespace HistoryAurora.Module;

/// <summary>
/// 在宿主进程内把界面开起来（DEC-008）。
///
/// Aurora 不再有独立 exe：界面是宿主装载的一个模块，启动、显示、隐藏一律走注册的指令，
/// 桌面入口由 Mercury 活动坞承担。本类是那条路径的落点。
///
/// **必须与 manifest 的 <c>pinned: true</c> 配套。** WPF 的类型解析、Dispatcher 与资源
/// 程序集都是进程级的，装进可回收上下文再卸载重来必崩在第二次；而实测一次冷启动就重载
/// 2–3 次。钉住之后同名程序集只装载一次，下面这些静态字段跨重载存活，
/// <see cref="EnsureStarted"/> 因此能靠它们做幂等守卫。
/// </summary>
internal static class AuroraShellHost
{
    private static readonly object Gate = new();

    private static Thread? _uiThread;

    private static ShellWindow? _window;

    /// <summary>界面就绪信号。宿主在 CreateUi 阶段就要取注册器，那时 STA 线程可能还没建完窗口。</summary>
    private static readonly ManualResetEventSlim Ready = new(false);

    /// <summary>
    /// 界面注册器，供宿主转交给其余 UI 模块。
    /// 界面尚未就绪时为 null——宿主据此整段跳过 UI 生命周期，不会崩在空引用上。
    /// </summary>
    internal static IShellUiRegistrar? Registrar => _window?.ShellUi;

    /// <summary>当前主窗口；仅供本模块的指令使用。</summary>
    internal static ShellWindow? Window => _window;

    /// <summary>
    /// 幂等启动。第二次及以后的调用直接返回——钉住模块每次重载都会重新 Attach，
    /// 不守卫就会开出第二套界面。
    /// </summary>
    internal static void EnsureStarted(IModuleContext context, TimeSpan readyTimeout)
    {
        lock (Gate)
        {
            // 只跳过**建线程**，不跳过后面的登记：宿主每次重载都会重建注册表，
            // 界面指令必须每轮都重新登记进去。早期版本在这里直接 return，
            // 症状是第一次装载能用、任何一次重载之后 aurora.ui.* 全变"未知指令"。
            if (_uiThread == null)
            {
                _uiThread = new Thread(() => RunUi(context))
                {
                    Name = "HistoryAurora.Shell",
                    // 后台线程：宿主的服务循环拥有进程寿命，界面不该反过来把宿主钉住不退。
                    IsBackground = true,
                };
                _uiThread.SetApartmentState(ApartmentState.STA);
                _uiThread.Start();
            }
        }

        // 阻塞等待窗口建好。宿主紧接着就要取注册器分发给其余 UI 模块，
        // 拿到 null 会让那些模块这一轮全部无处注册，而下一轮重载才补上——
        // 表现为"第一次启动少几个页面"，比多等几百毫秒难查得多。
        if (!Ready.Wait(readyTimeout))
        {
            context.Log.Warn("aurora", $"界面未在 {readyTimeout.TotalSeconds:0.#}s 内就绪，本轮模块页面可能缺失");
            return;
        }

        PublishShellCommands(context);
    }

    /// <summary>
    /// 把界面自己注册的那批命令登记进**宿主**注册表。
    ///
    /// 界面有一张自己的注册表（它当初是独立进程）。进程外时代靠网关把目录同步过去，
    /// 宿主再按 ConnectedShells &gt; 0 决定中继；进程内既没有连接也没有 socket，
    /// 那条闸门永远是 0，症状是任何 aurora.ui.* 都答"前端不可用"——
    /// 而 `vulcan.command.domains` 里却能看到它们（那是旧缓存留下的幽灵代理）。
    ///
    /// 这里改为直接登记：命令随模块进宿主注册表，由宿主随模块一起回收，
    /// 执行时编组到界面线程。中继与目录同步在进程内彻底不参与。
    /// </summary>
    private static void PublishShellCommands(IModuleContext context)
    {
        var window = _window;
        if (window == null)
            return;

        var registry = window.Commands.Registry;
        var owned = registry.All()
            .Where(descriptor => string.Equals(
                registry.GetSource(descriptor.Name),
                FrontendCommandCatalog.Source,
                StringComparison.Ordinal))
            .ToList();

        // 判据必须看宿主的**实时**注册表，不是 RegisterCommands 给的暂存表。
        //
        // 界面的注册表里除了它自己实现的命令，还有一批**为宿主命令建的代理**
        // （FrontendCommandCatalog 的框架代理，它们的实现就是返回"前端不可用"）。
        // 这两类的来源标签相同，按暂存表判重时暂存表是空的，于是代理也被登记回宿主，
        // 把 vulcan.app.show 这类真实实现覆盖成死代理——症状是命令还在、
        // 一调就答"前端不可用"，而窗口明明开着。
        var live = context.Bus.Registry;
        context.RegisterCommands(host =>
        {
            foreach (var descriptor in owned)
            {
                if (live.TryGet(descriptor.Name, out _) || host.TryGet(descriptor.Name, out _))
                    continue;
                host.Register(Marshalled(descriptor, window));
            }
        });

        context.Log.Info("aurora", $"已向宿主登记界面指令 {owned.Count} 条");
    }

    /// <summary>
    /// 代理描述符：元数据照抄，执行体编组到界面线程。
    /// 直接把原 Handler 挂进宿主注册表是不行的——宿主的命令跑在线程池上，
    /// 而这些命令要碰窗口。
    /// </summary>
    private static CommandDescriptor Marshalled(CommandDescriptor source, ShellWindow window)
        => new()
        {
            Name = source.Name,
            Domain = source.Domain,
            CommandClass = source.CommandClass,
            Summary = source.Summary,
            Example = source.Example,
            Parameters = source.Parameters,
            ConfirmPrompt = source.ConfirmPrompt,
            Dangerous = source.Dangerous,
            Readonly = source.Readonly,
            RequiresUiThread = source.RequiresUiThread,
            ExecutionSite = source.ExecutionSite,
            AllowMcpExecution = source.AllowMcpExecution,
            AllowUnspecifiedParameters = source.AllowUnspecifiedParameters,
            Annotations = source.Annotations,
            Handler = context => window.Dispatcher.Invoke(() => source.Handler(context)),
        };

    private static void RunUi(IModuleContext context)
    {
        try
        {
            var paths = new AppPaths(AppIdentity.Current.Name);
            var config = CreateConfig();
            var window = new ShellWindow(
                config,
                new FileLayoutStore(paths),
                context.Log,
                context.Settings,
                context.DataDirectory);

            _window = window;
            WireBuses(window, context);

            // 未处理异常落日志而不弹框：本进程是服务，没有人在屏幕前等着点"确定"。
            Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
            {
                context.Log.Log(ShellLogLevel.Fatal, "aurora", $"界面未处理异常: {args.Exception}");
                args.Handled = true;
            };

            window.Show();
            Ready.Set();
            context.Log.Info("aurora", "界面已在宿主进程内启动");

            // 不建 Application：主题字典挂在 ShellWindow.Resources 与 DockManager.Resources 上，
            // 全仓只有一处读 Application.Current 且本就空值守卫。少一个进程级单例，
            // 也就少一处"同一进程只能建一次"的重载障碍。
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            context.Log.Log(ShellLogLevel.Fatal, "aurora", $"界面启动失败: {ex}");
            Ready.Set();
        }
    }

    /// <summary>
    /// 进程内界面的配置。与迁入前独立进程版的关键差别是**两个模块开关都关掉**：
    /// 宿主自己的 ModuleHost 才是模块生命周期的所有者，界面再建一套就会把每个模块装载两遍。
    /// </summary>
    internal static ShellConfig CreateConfig()
    {
        var identity = AppIdentity.Current;
        var config = new ShellConfig
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            EnableModules = false,
            EnableUiModules = false,
            EnableMcp = false,
            RequireConfirmedModuleSources = true,
            EnableRemoteManagementViews = true,
            CloseBehavior = ShellCloseBehavior.Hide,
        };

        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.Modules,
            Title = "模块管理",
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.38,
        });

        return config;
    }

    /// <summary>
    /// 把界面的指令总线接到宿主总线上。
    ///
    /// ShellWindow 自带一套注册表与总线——它当初是独立进程，靠 RemoteExecutor 经 IPC
    /// 打到宿主。进程内没有 IPC 了，但那条路由仍然有用：把"远端"换成宿主的总线对象即可，
    /// 不必让界面直接改用宿主注册表（那会让界面自己的 aurora.ui.* 与宿主命令挤在一张表里，
    /// 卸载时也无法整体回收）。
    ///
    /// 不接的症状很具体：界面上点"刷新模块"报 `未知指令: vulcan.module.reload`——
    /// 那条命令在宿主注册表里，而界面查的是自己那张。
    /// </summary>
    private static void WireBuses(ShellWindow window, IModuleContext context)
    {
        window.Commands.RemoteExecutor = context.Bus.ExecuteAsync;
        window.Commands.ShouldUseRemoteCommand = (text, source) =>
        {
            // 宿主中继回来的命令就地执行，否则会在两条总线之间来回弹。
            if (source.Equals("Service:Relay", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var parsed = CommandParser.Parse(text.Trim());

                // 本机已登记且需要 UI 线程的命令留在界面侧：宿主那边没有窗口实例。
                if (window.Commands.Registry.TryGet(parsed.Name, out var descriptor)
                    && descriptor.RequiresUiThread
                    && descriptor.ExecutionSite != CommandExecutionSite.Frontend)
                    return false;
            }
            catch (CommandSyntaxException)
            {
            }

            return true;
        };

        // 反向：宿主收到界面命令时打回来。进程内直接指向界面总线，不经网关。
        context.Bus.FrontendExecutor = (text, source, cancellation)
            => window.Dispatcher.Invoke(() => window.Commands.ExecuteAsync(text, source, cancellation));
    }

    /// <summary>把动作编组到界面线程；界面未就绪时返回 false。</summary>
    internal static bool Invoke(Action action)
    {
        var window = _window;
        if (window == null)
            return false;
        window.Dispatcher.Invoke(action);
        return true;
    }
}
