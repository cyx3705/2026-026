using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Components.Modules;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryAurora.Shell.Neutral.Storage;
using HistoryAurora.Shell.Composition;
using HistoryAurora.Shell.Neutral.Commands;


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
/// <remarks>
/// 1.29.0 起只用宿主 6.0.0 的统一契约：界面各处的总线是宿主那条（<see cref="ShellBus"/> 只是加了一份目录），
/// Aurora 自己的指令原样登记进宿主、声明界面线程由宿主编组；日志与目录变化走总线主题。
/// 此前界面自建第二套总线与注册表，把指令逐字段抄成代理登记进宿主，并直接挂宿主注册表与日志的 C# 事件。
/// </remarks>
internal static class AuroraShellHost
{
    /// <summary>宿主产品名：窗口标题与关于框用。</summary>
    internal const string HostName = "HistoryVulcan";

    private static readonly object Gate = new();

    private static Thread? _uiThread;

    private static ShellWindow? _window;

    /// <summary>控制台显示的宿主日志视图；Shutdown 时退订，否则宿主一直握着旧加载上下文里的实例。</summary>
    private static MemoryShellLog? _consoleLog;

    /// <summary>目录变化的订阅；Shutdown 时退订。</summary>
    private static IDisposable? _catalogSubscription;

    /// <summary>模块变化的订阅（刷新模块页）；Shutdown 时退订。</summary>
    private static IDisposable? _moduleSubscription;

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
            // 界面指令必须每轮都重新登记进去。
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
        // 拿到 null 会让那些模块这一轮全部无处注册，而下一轮重载才补上。
        if (!Ready.Wait(readyTimeout))
            return;

        PublishShellCommands(context);
    }

    /// <summary>
    /// 把界面自己那批指令原样登记进宿主（1.29.0）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 要碰窗口的界面指令都声明 <see cref="CommandDescriptor.RequiresUiThread"/>：宿主执行时编组到前端登记的界面线程
    /// （<see cref="AuroraFrontend.UiContext"/>），已在界面线程上时就地执行。不再需要逐字段抄一份代理描述符再包一层
    /// Dispatcher——那种抄法漏一个字段，那项声明就在进宿主时被静默清空。
    /// 刻意不声明的（如 <c>aurora.log.snapshot</c>：界面卡住时 AI 仍要读得到控制台）在线程池上执行，自己负责线程安全；
    /// 契约测试逐条核对这张名单。
    /// </para>
    /// <para>
    /// 宿主已有的同名指令跳过：<c>vulcan.app.show/hide/close/focusconsole</c> 与共享内置指令由宿主定义，
    /// 它们在界面表里只为独立运行（组件画廊、契约测试）而存在。
    /// </para>
    /// </remarks>
    private static void PublishShellCommands(IModuleContext context)
    {
        var window = _window;
        if (window == null)
            return;

        var host = window.Commands.Registry;
        var owned = window.OwnCommands.All();
        context.RegisterCommands(registrar =>
        {
            foreach (var descriptor in owned)
            {
                if (!host.TryGet(descriptor.Name, out _))
                    registrar.Register(descriptor);
            }
        });
    }

    private static void RunUi(IModuleContext context)
    {
        try
        {
            var dataDirectory = context.Environment.DataDirectory;
            var paths = AuroraPaths.ForDataDirectory(dataDirectory);
            var config = CreateConfig(context.Environment.HostVersion);
            // 控制台只显示宿主那一份日志（DEC-042）：界面自己的记录也写进去，不另建第二份。
            var log = HostLogFeed.Create(context);
            lock (Gate)
                _consoleLog = log;

            var catalog = ShellCatalog.FromHost(context.Bus);
            var catalogSubscription = context.Subscribe("vulcan.catalog.changed", _ => catalog.Invalidate());
            lock (Gate)
                _catalogSubscription = catalogSubscription;

            var settings = new JsonSettingsStore(paths.SettingsFile);
            // 自己的指令表要同时交给总线：建窗时菜单按它校验，那时这些指令还没登记进宿主（见 PublishShellCommands）。
            var ownCommands = new CommandTable();
            var window = new ShellWindow(
                config,
                new FileLayoutStore(paths.LayoutDir),
                log,
                settings,
                paths.Root,
                new ShellBus(context.Bus, catalog, ownCommands),
                ownCommands);

            _window = window;
            window.EnableHostIntegration();
            // 1.29.1：模块装上、卸下、接入失败都发 vulcan.module.changed（失败时目录不变，只看目录会漏），模块页据此重取。
            var moduleSubscription = context.Subscribe("vulcan.module.changed", _ => window.RefreshHostPage("modules"));
            lock (Gate)
                _moduleSubscription = moduleSubscription;

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
            // 界面起不来必须看得见：写宿主日志（控制台没起来时它照样落盘）。只写调试输出等于没人知道。
            context.Log.Log(ShellLogLevel.Fatal, "aurora", $"界面启动失败: {ex}");
            Ready.Set();
        }
    }

    /// <summary>
    /// 进程内界面的配置。与迁入前独立进程版的关键差别是**两个模块开关都关掉**：
    /// 宿主自己的 ModuleHost 才是模块生命周期的所有者，界面再建一套就会把每个模块装载两遍。
    /// </summary>
    internal static ShellConfig CreateConfig(string hostVersion)
    {
        var config = new ShellConfig
        {
            AppName = HostName,
            AppVersion = hostVersion,
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
    /// 执行宿主转交的界面生命周期指令（<see cref="IFrontend.ExecuteAsync"/>）。宿主只转这四条。
    /// </summary>
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

        // 不能转回总线：宿主正是经 IFrontend 把这条交过来的，再发回去就是一个来回弹的环。
        return Task.FromResult(CommandResult.Fail($"前端不处理 {name}"));
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
                // 不在这里立刻 Discover：宿主此刻往往还在逐条登记命令。
                // ScheduleDiscover 等目录安静 150ms 再拉一轮。
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
    internal static void Shutdown(IModuleLog? log)
    {
        Thread? uiThread;
        ShellWindow? window;
        MemoryShellLog? consoleLog;
        IDisposable? catalogSubscription;
        IDisposable? moduleSubscription;
        lock (Gate)
        {
            window = _window;
            uiThread = _uiThread;
            consoleLog = _consoleLog;
            catalogSubscription = _catalogSubscription;
            moduleSubscription = _moduleSubscription;
            _window = null;
            _uiThread = null;
            _consoleLog = null;
            _catalogSubscription = null;
            _moduleSubscription = null;
            Ready.Reset();
        }

        // 先退订：界面线程即使卡住没退出，宿主也不再往旧实例里送事件。
        consoleLog?.Dispose();
        catalogSubscription?.Dispose();
        moduleSubscription?.Dispose();

        if (window == null && uiThread == null)
            return;

        var onUiThread = uiThread != null && ReferenceEquals(Thread.CurrentThread, uiThread);

        void CloseAndStopDispatcher()
        {
            try
            {
                window?.DisableHostIntegration();
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

/// <summary>
/// 控制台的宿主日志来源（1.29.0）：新纪录订阅 <c>vulcan.log.entry</c>，旧记录执行 <c>vulcan.log.recent</c> 补读。
/// 载荷按宿主模块API写明的字段读：<c>time / level / category / message</c>。
/// </summary>
internal static class HostLogFeed
{
    internal static MemoryShellLog Create(IModuleContext context)
        => new(
            context.Log,
            handler => context.Subscribe("vulcan.log.entry", evt =>
            {
                if (Parse(evt.Payload) is { } entry)
                    handler(entry);
            }),
            () =>
            {
                var recent = context.Bus
                    .InvokeAsync($"vulcan.log.recent count={MemoryShellLog.BacklogImportLimit}", "")
                    .GetAwaiter()
                    .GetResult();
                return recent.Success && recent.Data is JsonElement { ValueKind: JsonValueKind.Array } rows
                    ? rows.EnumerateArray().Select(Parse).OfType<ShellLogEntry>().ToList()
                    : [];
            });

    internal static ShellLogEntry? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("message", out var message)
            || !payload.TryGetProperty("category", out var category))
            return null;
        var time = payload.TryGetProperty("time", out var stamp) && stamp.TryGetDateTime(out var parsed)
            ? parsed
            : DateTime.Now;
        var level = payload.TryGetProperty("level", out var levelText)
                    && Enum.TryParse<ShellLogLevel>(levelText.GetString(), ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : ShellLogLevel.Info;
        return new ShellLogEntry(time, level, category.GetString() ?? "", message.GetString() ?? "");
    }
}
