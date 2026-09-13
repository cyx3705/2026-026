using System.Windows;
using System.Windows.Threading;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Components.Modules;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryAurora.Shell.Neutral.Storage;
using HistoryAurora.Shell.Composition;


namespace HistoryAurora.Module;

/// <summary>
/// 在宿主进程内把界面开起来（DEC-008）。
///
/// Aurora 不再有独立 exe：界面是宿主装载的一个模块，启动、显示、隐藏一律走注册的指令，
/// 桌面入口由 Mercury 活动坞承担。本类是那条路径的落点。
///
/// 热重载要求宿主先 DestroyUi 再装新包。本类在 Shutdown 里关掉窗口、停 Dispatcher、
/// 汇合 STA 线程，静态字段归零；下一轮 <see cref="EnsureStarted"/> 会再开一条界面线程。
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
    /// 幂等启动。同一轮装载里 Attach 可能被叫两次，不得开出第二套界面。
    /// 热重载会先 Shutdown，静态字段归零后再进来，那时应当新开线程。
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
            return;

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
            Level = source.Level,
            Readonly = source.Readonly,
            RequiresUiThread = source.RequiresUiThread,
            // 逐字段抄写就得抄全：漏掉一个，界面指令的那项声明会在进入宿主表时被静默清空。
            // 隐藏声明尤其不能漏——漏掉等于把一条刻意不对远端暴露的指令暴露出去。
            HiddenReason = source.HiddenReason,
            AllowUnspecifiedParameters = source.AllowUnspecifiedParameters,
            Annotations = source.Annotations,
            Handler = context =>
            {
                // 已经在界面线程上时不得 Dispatcher.Invoke：选文件的 ShowDialog
                // 自己会开一层消息泵，外层再同步等同一条 Dispatcher 就会把整窗卡死。
                if (window.Dispatcher.CheckAccess())
                    return source.Handler(context);
                return window.Dispatcher.InvokeAsync(() => source.Handler(context)).Task.Unwrap();
            },
        };

    private static void RunUi(IModuleContext context)
    {
        try
        {
            var paths = AuroraPaths.ForApplication(AppIdentity.Current.Name);
            var config = CreateConfig();
            var log = new MemoryShellLog();
            var settings = new JsonSettingsStore(paths.SettingsFile);
            var window = new ShellWindow(
                config,
                new FileLayoutStore(paths.LayoutDir),
                log,
                settings,
                paths.Root);

            _window = window;
            WireBuses(window, context);

            // 未处理异常落日志而不弹框：本进程是服务，没有人在屏幕前等着点"确定"。
            Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
            {
                log.Log(ShellLogLevel.Fatal, "aurora", $"界面未处理异常: {args.Exception}");
                args.Handled = true;
            };

            Ready.Set();

            // 不建 Application：主题字典挂在 ShellWindow.Resources 与 DockManager.Resources 上，
            // 全仓只有一处读 Application.Current 且本就空值守卫。少一个进程级单例，
            // 也就少一处"同一进程只能建一次"的重载障碍。
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"aurora 界面启动失败: {ex}");
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
        // 命令集回到中央主区（1.7.0）。宿主 5.0 拆掉界面 SDK 后这一页没人挂，
        // 主区因此空了一轮；它现在由 Aurora 自建，不再取决于哪个模块在不在场。
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.Mcp,
            Title = "命令集",
            DefaultSide = DockSide.Center,
            DefaultRatio = 0.5,
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.CommandDetail,
            Title = "指令详情",
            DefaultSide = DockSide.Right,
            DefaultRatio = 0.26,
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
            ShouldUseRemote(window.Commands.Registry, context.Bus.Registry, text, source);

        // 反向中继、二次确认与界面线程不在这里改写宿主总线：宿主 5.4 起由 AuroraFrontend
        // 经 IModuleContext.RegisterFrontend 一次登记（见 AuroraBusinessComposition.Attach）。

        window.AttachHostBus(context.Bus);
    }

    /// <summary>
    /// 这条命令该发给宿主，还是就地执行？
    ///
    /// 默认发给宿主：界面的命令在 Attach 时由 <see cref="PublishShellCommands"/> 抄进宿主
    /// 注册表，宿主才是对外的单一目录。两个例外：
    ///
    /// <list type="number">
    ///   <item>宿主中继回来的命令就地执行，否则会在两条总线之间来回弹。</item>
    ///   <item><b>本机有、宿主没有的命令就地执行。</b>发过去只会换回一条「未知指令」——
    ///         这是 1.8.9～1.8.12 连查四轮的那条实测故障：`aurora.preview.rows` /
    ///         `.graph` 在界面注册表里明明在，一执行却报未知，因为它们没能进宿主注册表。
    ///         为什么没进去是宿主那一侧的事；但**不管为什么，把一条本机能跑的命令
    ///         发出去换回"不认识"都是错的**，所以判据改成看宿主到底有没有，
    ///         而不是假定发布一定成功。</item>
    /// </list>
    ///
    /// 需要 UI 线程的命令同样留在界面侧：宿主那边没有窗口实例。
    /// </summary>
    internal static bool ShouldUseRemote(
        CommandRegistry local,
        CommandRegistry host,
        string text,
        string source)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(host);

        if (source.Equals("Service:Relay", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var parsed = CommandParser.Parse(text.Trim());
            if (!local.TryGet(parsed.Name, out var descriptor))
                return true;

            if (descriptor.RequiresUiThread)
                return false;

            return host.TryGet(parsed.Name, out _);
        }
        catch (CommandSyntaxException)
        {
            return true;
        }
    }

    internal static Task<CommandResult> DispatchFrontendCommand(
        ShellWindow window, string name, string source, CancellationToken cancellation)
    {
        if (name.Equals("vulcan.app.show", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vulcan.app.focusconsole", StringComparison.OrdinalIgnoreCase))
        {
            if (!window.IsVisible)
                window.Show();
            window.Activate();
            if (name.Equals("vulcan.app.focusconsole", StringComparison.OrdinalIgnoreCase))
                window.ActivateToolContent("console");
            return Task.FromResult(CommandResult.Ok("界面已显示"));
        }

        if (name.Equals("vulcan.app.hide", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vulcan.app.close", StringComparison.OrdinalIgnoreCase))
        {
            window.Hide();
            return Task.FromResult(CommandResult.Ok("界面已隐藏"));
        }

        return window.Commands.ExecuteAsync(name, source, cancellation);
    }

    /// <summary>
    /// Attach 返回后排队显示。此时宿主往往还在提交模块快照；
    /// ApplicationIdle 让 Show 落到快照提交之后，模块管理页才能读到清单。
    /// </summary>
    internal static void ShowMainWindowIdle()
    {
        var window = _window;
        if (window == null)
            return;

        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                ShowMainWindow();
                // 不在这里立刻 Discover：宿主此刻往往还在逐条登记命令，
                // 每条都会 Changed，立刻拉会与那一轮风暴叠在一起。
                // ScheduleDiscover 等注册表安静 150ms 再拉一轮。
                window.ScheduleDiscover();
            }));
    }

    /// <summary>
    /// 宿主 <c>CreateUi</c> 阶段再 Show。模块清单此时已经 Finalize，
    /// 模块管理页 Loaded 读 <c>vulcan.module.list</c> 才能看见运行区里的包。
    /// </summary>
    internal static void ShowMainWindow()
    {
        var window = _window;
        if (window == null)
            return;

        void Show()
        {
            if (!window.IsVisible)
                window.Show();
        }

        if (window.Dispatcher.CheckAccess())
            Show();
        else
            window.Dispatcher.Invoke(Show);
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

    /// <summary>
    /// 关掉窗口、停 Dispatcher、汇合 STA 线程。热重载与宿主退出都走这里。
    /// 已在界面线程上时不得 Join 自己。
    /// </summary>
    internal static void Shutdown(IShellLog? log)
    {
        Thread? uiThread;
        ShellWindow? window;
        lock (Gate)
        {
            window = _window;
            uiThread = _uiThread;
            _window = null;
            _uiThread = null;
            Ready.Reset();
        }

        if (window == null && uiThread == null)
            return;

        var onUiThread = uiThread != null && ReferenceEquals(Thread.CurrentThread, uiThread);

        void CloseAndStopDispatcher()
        {
            try
            {
                window?.DetachHostBus();
                window?.ForceClose();
            }
            catch (Exception ex)
            {
                log?.Warn("aurora", $"关闭界面失败: {ex.Message}");
            }

            var dispatcher = window?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        if (window != null)
        {
            if (onUiThread)
            {
                CloseAndStopDispatcher();
            }
            else
            {
                try
                {
                    window.Dispatcher.Invoke(CloseAndStopDispatcher);
                }
                catch (Exception ex)
                {
                    log?.Warn("aurora", $"编组关闭界面失败: {ex.Message}");
                }
            }
        }

        if (uiThread != null && !onUiThread && !uiThread.Join(TimeSpan.FromSeconds(5)))
            log?.Warn("aurora", "界面线程 5s 内未退出，新一轮装载可能与旧窗口重叠");
    }
}
