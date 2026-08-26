using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.CommandSurface;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell.Modules;
using HistoryAurora.Shell.Console;
using HistoryAurora.Shell.Themes;
using AvalonDock.Controls;
using AvalonDock.Themes;
using AvalonDock.Layout;

namespace HistoryAurora.Shell;

/// <summary>
/// 主程序窗体(Main Frame,§2)。3.1 起为自绘顶栏 + 停靠系统容器两段结构:
/// 菜单折叠进顶栏右上角的菜单按钮(UI-03),常驻菜单行与底部状态栏均已取消
/// (UI-05,原状态栏信息改由顶栏徽章、布局文本和瞬时回执承担)。
/// M2 起指令总线为一切操作的汇聚点:菜单项点击同样是发指令(S-02),
/// 控制台手输、脚本、布局手势与派生应用共用同一张指令注册表。
/// </summary>
internal partial class ShellWindow : Window, IShellCommandWorkbenchHost
{
    private readonly ShellConfig _config;
    private readonly IShellLog _log;
    private readonly DockingHost _docking;
    private readonly string _dataDirectory;
    private readonly CommandBus _bus;
    private readonly CommandSelectionState _commandSelection;
    private readonly CommandHistory _history;
    private readonly DeferredCommandCatalogSession _catalogSession;
    private readonly LocalCommandCatalogSession _catalog;
    private readonly ConsoleView _console;
    private readonly Actions.ActionRegistry _actions;

    private readonly Panels.PanelManager _panels;

    private readonly Pages.ModulePageLoader _pageLoader;
    private readonly Pages.ComponentRequestStore _componentRequests;
    private readonly Modules.ShellUiRegistrar _shellUi;
    private Modules.UiAnnotationClaimer? _annotationClaimer;
    private Action? _hostRegistryChanged;

    // 命令集页与指令详情页的选中联动(0.4.4 上抛):优先用派生应用经 ShellConfig 传入的实例,
    // 未传则自建。由构造函数赋值——工具窗口内容工厂在 DockingHost 构建默认布局时即被调用,
    // 派生应用那时拿不到 window,故联动实例必须由派生侧创建并传入。
    // UI-03:折叠后的菜单挂在顶栏菜单按钮上(挂上去才能继承窗体资源与样式)
    private readonly ContextMenu _menu = new();

    private int _errorCount;
    private bool _menusInitialized;

    // UI-09.1:true 表示已接管窗体非客户区;false 表示宿主改过 WindowStyle,
    // 只降级边框接管方式,顶栏四个按钮仍然保留并可用。
    private bool _customChrome;

    // Alt 单独按下(未与其他键组合)才呼出菜单,避免抢走 Alt+Tab 等组合
    private bool _altPressedAlone;

    // 主题字典副本:窗格基底样式与 3.1 卡片/专注模板都从这里取
    private ResourceDictionary? _themeResources;

    // UI-08:浅色/深色令牌整份切换(aurora.app.theme),设置项持久化
    private readonly ISettingsService _settings;
    private string _theme = ThemeLight;

    /// <summary>弹窗与主窗体必须用同一套令牌；独立 Window 自己合并字典，但要知道此刻是哪一套。</summary>
    internal bool IsDarkTheme => string.Equals(_theme, ThemeDark, StringComparison.Ordinal);

    // 右上角按钮组要在最上一排页签里占位,避免页签跑到按钮底下
    private readonly DispatcherTimer _chromeUpkeep;
    private bool _reservePending;
    private bool _closing;
    private bool _allowClose;
    private ContentControl? _chromeHost;
    private FrameworkElement? _chromeDragSurface;
    private readonly ShellTopBarCoordinator _topBar;

    public ShellWindow(
        ShellConfig config,
        ILayoutStore layoutStore,
        IShellLog log,
        ISettingsService settings,
        string dataDirectory)
    {
        InitializeComponent();

        _config = config;
        _log = log;
        _dataDirectory = dataDirectory;
        _settings = settings;
        Title = $"{config.AppName} v{config.AppVersion}";

        // UI-03:菜单按钮的弹出层;挂到按钮上才能继承窗体资源(菜单项样式)
        MenuButton.ContextMenu = _menu;

        // AvalonDock 主题字典必须先于令牌换位就位：AuroraTheme.xaml 内部合并了
        // AuroraTokens.xaml(浅色)，而 SwapTokens 是把令牌追加到 MergedDictionaries 末尾、
        // 且幂等不重排。先换令牌再赋值 Theme 的话，令牌停在索引 0、主题字典排到它后面，
        // WPF 后者胜出 —— 表现为「设置里是深色，启动却是浅色，手动再切一次才对」。
        // 按**实例**下发,不让 AvalonDock 按 URI 重新解析(REQ-UI-029)。
        // 覆盖窗(拖动浮窗时那组蓝色方位指示)是独立 Window,它构造时会照着
        // Theme.GetResourceUri() 再解析一遍字典;那条路在宿主里不成立,失败又不抛,
        // 于是覆盖窗带着 0 份字典活下来、一个像素都不画。DictionaryTheme 直接传实例。
        DockManager.Theme = new AuroraTheme(
            (ResourceDictionary)Resources["AuroraDockTheme"]);

        // UI-08:上次选择的主题先于任何界面成型生效,避免启动瞬间闪一下浅色
        ApplyTheme(settings.Get(ThemeSettingsKey) ?? ThemeLight, persist: false);

        StateChanged += (_, _) => ApplyWindowStateChrome();
        SourceInitialized += OnShellSourceInitialized;
        PreviewKeyDown += OnShellPreviewKeyDown;
        PreviewKeyUp += OnShellPreviewKeyUp;

        // 主窗体边界先于停靠布局恢复:布局像素尺寸相对窗体记录,
        // 窗体尺寸一致才能做到“重启后布局原样恢复”(验收 1 / 3)
        RestoreWindowBounds();

        LoadThemeResources();
        ApplyPaneStyles(chromeless: false);

        // ---- 指令核心(§5):注册表 + 总线 + 历史 + 控制台（命令集/详情由 Mercury 挂载）
        var registry = new CommandRegistry();
        _bus = new CommandBus(registry, log)
        {
            UiContext = SynchronizationContext.Current,
            Confirmation = new MessageBoxConfirmation(this),
        };
        _commandSelection = config.CommandSelection ?? new CommandSelectionState();
        _history = new CommandHistory(
            Path.Combine(dataDirectory, "history.txt"),
            settings.GetInt(ConsoleView.KeyHistory, 500));
        // 传入本地注册表:Mercury 未挂接时控制台的域/类过滤仍按本地权威目录工作(DEC-023)。
        _catalogSession = new DeferredCommandCatalogSession(registry);

        // 命令目录会话由 Aurora 自建并当场挂上（REQ-UI-014）。
        // 5.0 之前这个会话来自 Mercury 的命令工作台，宿主拆掉界面 SDK 后没人再挂，
        // 于是命令集与指令详情两页消失、控制台的 Tab 补全也一并成了死路。
        // 模块日后仍可 Attach 自己的实现覆盖它，但"没有模块"不再等于"没有目录"。
        _catalog = new LocalCommandCatalogSession(_bus, log);
        _catalogSession.Attach(_catalog);
        _console = new ConsoleView(
            log,
            _bus,
            _history,
            _catalogSession,
            settings.GetInt(ConsoleView.KeyBuffer, 50_000));
        TakeOverDescriptor(
            StandardWindowIds.Console,
            "控制台",
            DockSide.Bottom,
            0.25,
            () => _console);

        if (config.EnableModules || config.EnableRemoteManagementViews)
        {
            TakeOverDescriptor(StandardWindowIds.Modules, "模块管理", DockSide.Right, 0.32,
                () => new Views.ModulesView(() => _bus));
        }

        // 命令集是中央主文档（DockingHost 按这个 id 认"主窗口"）；指令详情停在右侧与它联动。
        TakeOverDescriptor(StandardWindowIds.Mcp, "命令集", DockSide.Center, 0.5,
            () => new Views.CommandCatalogView(_catalog, _bus, log, _commandSelection));
        TakeOverDescriptor(StandardWindowIds.CommandDetail, "指令详情", DockSide.Right, 0.28,
            () => new Views.CommandDetailView(_catalog, _bus, _commandSelection));

        // 动作声明台账要早于面板:面板按钮绑的是动作 id,构建时就要能解析。
        // 首次拉取不在这里做——那时模块还没装载,问谁都是空。见 DiscoverModuleSurfacesAsync。
        _actions = new Actions.ActionRegistry(_bus, log);

        // 控制窗口群:JSON + C# 通道合并,每个面板一个可停靠窗口
        _panels = new Panels.PanelManager(
            HistoryVulcan.Services.AppPaths.GetPanelsDir(dataDirectory),
            config.Panels,
            _bus,
            log,
            _actions);
        _panels.RegisterWindows(config.ToolWindows);

        _docking = new DockingHost(DockManager, config.ToolWindows, layoutStore, log, settings);
        _docking.Initialize();
        ConfigureCommandCompletionRouting(
            () => _docking.MaximizedId?.Equals(
                StandardWindowIds.Console,
                StringComparison.OrdinalIgnoreCase) == true,
            () =>
            {
                _ = ShowCommandCatalogForCompletionAsync();
            });
        _topBar = new ShellTopBarCoordinator(
            this,
            DockManager,
            _docking,
            _bus,
            _log,
            (RoutedCommand)Resources["Aurora.Command.PageAction"]);
        // 按钮组占位与浮动窗口主题需要在「布局稳定之后」才算得准,但不能挂
        // LayoutUpdated:那个事件每帧都发,回调里任何写操作都会再触发一次布局,
        // 直接转成 100% CPU 的死循环(实测)。改为低频巡检 + 幂等写入。
        _chromeUpkeep = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _chromeUpkeep.Tick += (_, _) =>
        {
            if (_closing)
                return;
            AttachChromeBarToMainDocumentPane();
            ReserveSpaceForChromeBar();
            ApplyThemeToFloatingWindows();
        };
        Loaded += (_, _) => _chromeUpkeep.Start();
        _shellUi = new Modules.ShellUiRegistrar(_docking, Dispatcher, log);
        _docking.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                BuildMenus();
            }
            catch (Exception ex)
            {
                _log.Error("menu", $"菜单重建失败，已保留上一版菜单: {ex.Message}");
            }
            UpdateLayoutIndicator();

            // UI-04:页面最大化态与常规态的窗格外观、顶栏形态在此切换。
            // WindowsChanged 是 MaximizeWindow / RestoreLayoutFromMaximized 的共同出口。
            ApplyFocusChrome();

            // R4-3:刚拖出来的浮动窗口带的是自己那份浅色令牌,补一次主题
            ApplyThemeToFloatingWindows();
            _topBar.Refresh();
        });

        // ---- 内置指令组 + 派生应用自定义指令(冲突此时报错,§5.3)
        // 页面注册协议 V1：拉取器要早于内置指令组构造，指令组才能拿到它。
        // 首次拉取不在这里做——那时模块还没装载，问谁都是空。见下方 ReloadCompleted。
        _componentRequests = new Pages.ComponentRequestStore(settings, log);
        _pageLoader = new Pages.ModulePageLoader(
            _bus, _docking, log, _componentRequests, _actions, _catalog.CompleteAsync);

        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = this,
            Docking = _docking,
            Console = _console,
            History = _history,
            Settings = settings,
            Log = log,
            Bus = _bus,
            DataDirectory = dataDirectory,
            Panels = _panels,
            Actions = _actions,
            PageLoader = _pageLoader,
            ComponentRequests = _componentRequests,
        });

        RegisterFrontendLifecycleCommands(registry);

        // UI-08:主题切换也是一条指令(S-02),菜单项与控制台走同一条路径
        registry.Register(new CommandDescriptor
        {
            Name = "aurora.app.theme",
            Domain = "aurora",
            CommandClass = "app",
            Summary = "切换界面主题(浅色 / 深色)",
            Example = "aurora.app.theme mode=dark",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "light、dark 或 toggle",
                    Position = 0,
                    Default = "toggle",
                    AllowedValues = [ThemeLight, ThemeDark, "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.GetString("mode") ?? "toggle";
                if (mode.Equals("toggle", StringComparison.OrdinalIgnoreCase))
                    mode = _theme == ThemeDark ? ThemeLight : ThemeDark;
                ApplyTheme(mode, persist: true);
                return CommandResult.Ok($"界面主题已切换为 {_theme}");
            }),
        });
        // ---- 0.4.4 反哺能力:模块托管与 MCP 服务(默认关闭,ShellConfig 显式开启)
        // 5.0 起模块生命周期由宿主独占。界面不再自建 ModuleHost，也不再调用已删除的
        // EnableUiModules / ShellUi / CommandWorkbench / ModulePanelSync。
        if (config.EnableModules || config.EnableUiModules)
            log.Warn("shell", "5.0 起界面不再自建模块宿主，EnableModules/EnableUiModules 已被忽略");

        config.ConfigureCommands?.Invoke(registry);


        // 命令结果统一进入 IShellLog/控制台；失败仍自动打开控制台查看全文。
        _bus.Executed += (text, source, result) => Dispatcher.BeginInvoke(() =>
        {
            if (!result.Success)
            {
                FocusConsole(resetFilters: true, preserveMaximizedLayout: true);
            }
            UpdateLayoutIndicator();
        });
        log.EntryAdded += (_, entry) =>
        {
            if (entry.Level >= ShellLogLevel.Error)
            {
                Interlocked.Increment(ref _errorCount);
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateErrorBadge();
                    if (entry.Level >= ShellLogLevel.Fatal)
                        FocusConsole(resetFilters: true, preserveMaximizedLayout: true);
                });
            }
        };

        // DEC-023:原 C-15 的 Ctrl + ` 本地 KeyBinding 已删除。快捷键整体归 HistoryMercury
        // （DEC-022），Shell 不再自行注册 InputBinding；需要该手势时由 Mercury 注册并指向
        // aurora.log.focus，避免宿主与模块争夺同一组合键。

        if (config.EnableMaximizeOnDoubleClick)
        {
            DockManager.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnDockDoubleClick),
                handledEventsToo: true);
        }
        BuildMenus();
        UpdateLayoutIndicator();
        ApplyFocusChrome();

        Closing += OnShellClosing;
        Closed += OnShellClosed;
    }

    /// <summary>
    /// UI-02 / UI-09.1:非客户区接管必须推迟到窗体句柄就绪 —— 派生应用常在对象
    /// 初始化器里(即构造函数返回之后)设置 WindowStyle,构造期判定会误判成标准窗体。
    /// 用事件而非 override，避免仅为内部窗体时序扩大公开面。
    /// </summary>
    private void OnShellSourceInitialized(object? sender, EventArgs e)
    {
        if (WindowStyle == WindowStyle.SingleBorderWindow)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                // 不再让隐藏标题区横跨整个窗体顶部：它会吞掉工具窗格的 ▼/×。
                // 窗体拖动只由中央文档页签行的空白区域处理。
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                // Q-4:Win10 IoT 无系统窗口圆角,窗体外缘一律直角
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            _customChrome = true;
        }
        else
        {
            // 只降级边框接管方式;顶栏的菜单与三个窗口按钮保持不变(UI-09.1)
            _customChrome = false;
            _log.Info("shell", $"宿主使用 WindowStyle={WindowStyle},已跳过非客户区接管,顶部按钮组仍然可用");
        }

        ApplyWindowStateChrome();
        ScheduleChromeReserve();
    }

    /// <summary>停靠系统门面。</summary>
    public IDockingService Docking => _docking;

    /// <summary>指令总线(派生应用 / 启动参数经此执行指令)。</summary>
    public CommandBus Commands => _bus;

    /// <summary>
    /// 宿主总线。进程内装载后由 AuroraShellHost 注入，用来扫描模块注解并执行活对象命令。
    /// </summary>
    internal CommandBus? HostBus { get; private set; }

    internal void AttachHostBus(CommandBus hostBus)
    {
        ArgumentNullException.ThrowIfNull(hostBus);
        DetachHostBus();
        HostBus = hostBus;
        _annotationClaimer = new Modules.UiAnnotationClaimer(hostBus, _docking, _log);
        _hostRegistryChanged = () =>
        {
            Dispatcher.BeginInvoke(() => _ = DiscoverModuleSurfacesAsync());
        };
        hostBus.Registry.Changed += _hostRegistryChanged;
    }

    internal void DetachHostBus()
    {
        if (HostBus != null && _hostRegistryChanged != null)
            HostBus.Registry.Changed -= _hostRegistryChanged;
        _hostRegistryChanged = null;
        HostBus = null;
        _annotationClaimer = null;
    }

    /// <summary>拉取 <c>*.ui.describe</c> 页面并认领 <c>ui.window</c> 注解窗格。</summary>
    internal async Task DiscoverModuleSurfacesAsync()
    {
        try
        {
            // 动作声明先于页面:页面按钮与泳道的点击都要按 id 解析动作,
            // 顺序反了会在冷启动那一轮把每个按钮都判成「未声明」。
            await _actions.ReloadAsync().ConfigureAwait(true);
            RegisterComponentGallery();
            _panels.RebuildAll();
            await _pageLoader.ReloadAsync().ConfigureAwait(true);
            if (_annotationClaimer != null)
                await _annotationClaimer.ClaimAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Log(ShellLogLevel.Warn, "ui-claim", "模块界面发现失败: " + ex.Message);
        }
    }

    /// <summary>Adds a backend log entry to the in-memory console without writing a second log file.</summary>
    public void AddTransientLog(ShellLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The frontend command bus already records its local echo/result. Do not show
        // the same command categories again when the backend event stream arrives.
        if (entry.Category.StartsWith(CommandBus.EchoCategoryPrefix, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Equals(CommandBus.ResultCategory, StringComparison.OrdinalIgnoreCase))
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddTransientLog(entry));
            return;
        }

        _console.AddTransientEntry(entry);
        if (entry.Level < ShellLogLevel.Error)
            return;
        Interlocked.Increment(ref _errorCount);
        UpdateErrorBadge();
    }

    /// <summary>模块托管宿主。5.0 起由 HistoryVulcan 独占，界面侧恒为 null。</summary>
    public HistoryVulcan.Services.Modules.ModuleHost? Modules => null;

    /// <summary>
    /// 界面注册器。进程内形态下由模块交回宿主，宿主再转给其余 UI 模块——
    /// 宿主自己已不含任何界面实现，注册器只能来自这里（DEC-008）。
    /// </summary>
    public IShellUiRegistrar ShellUi => _shellUi;

    /// <summary>
    /// 命令集选中状态(0.4.4):框架的命令集窗口写入,派生应用的指令详情窗口读取。
    /// 派生应用应把自己的详情视图接到本实例,避免各建一个导致联动失效。
    /// </summary>
    public CommandSelectionState CommandSelection => _commandSelection;

    CommandBus IShellCommandWorkbenchHost.Bus => _bus;

    CommandSelectionState IShellCommandWorkbenchHost.CommandSelection => _commandSelection;

    ISettingsService IShellCommandWorkbenchHost.Settings => _settings;

    IShellLog IShellCommandWorkbenchHost.Log => _log;

    string IShellCommandWorkbenchHost.DataDirectory => _dataDirectory;

    /// <inheritdoc />
    public void AttachCommandCatalogSession(ICommandCatalogSession session)
        => _catalogSession.Attach(session);

    /// <inheritdoc />
    public void ConfigureCommandCompletionRouting(Func<bool> isConsoleFocused, Action showCommandCatalog)
        => _console.ConfigureCompletionRouting(isConsoleFocused, showCommandCatalog);

    /// <inheritdoc />
    public void RefreshCommandCompletionFocus() => _console.RefreshCompletionFocus();

    /// <summary>关于对话框文本(aurora.app.about)。</summary>
    public string AboutText =>
        $"{_config.AppName} v{_config.AppVersion}\n\n基于 HistoryVulcan 通用窗口框架模板\n.NET 8 + WPF + AvalonDock 4.72.1";

    /// <summary>
    /// 标准窗口内容接管:描述符的停靠位置仍由派生应用声明,内容工厂换成
    /// Shell 实现;未声明时按缺省位置强制注册(控制台是架构不变量 2 的落点)。
    /// 窗口 ID 是框架与派生应用之间的对接约定：双方各自声明并按 Id 合并，
    /// 不得仅在一侧改名；新增窗口时须同步核对派生应用的 ToolWindows 表。
    /// </summary>
    private void TakeOverDescriptor(
        string id,
        string fallbackTitle,
        DockSide fallbackSide,
        double fallbackRatio,
        Func<object> factory,
        bool forcePlacement = false)
    {
        var index = _config.ToolWindows.FindIndex(
            d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = id,
                Title = fallbackTitle,
                DefaultSide = fallbackSide,
                DefaultRatio = fallbackRatio,
                ContentFactory = factory,
            });
            return;
        }

        var d0 = _config.ToolWindows[index];
        _config.ToolWindows[index] = new ToolWindowDescriptor
        {
            Id = d0.Id,
            Title = d0.Title,
            DefaultSide = forcePlacement ? fallbackSide : d0.DefaultSide,
            DefaultRatio = forcePlacement ? fallbackRatio : d0.DefaultRatio,
            DefaultTabTarget = forcePlacement ? null : d0.DefaultTabTarget,
            DefaultVisible = d0.DefaultVisible,
            IsSingleton = d0.IsSingleton,
            ContentFactory = factory,
        };
    }

    private void FocusConsole(bool resetFilters = false, bool preserveMaximizedLayout = false)
    {
        if (resetFilters)
            _console.ResetFilters();
        if (preserveMaximizedLayout && _docking.MaximizedId != null)
        {
            if (_docking.MaximizedId.Equals(StandardWindowIds.Console, StringComparison.OrdinalIgnoreCase))
                ActivateToolContent(StandardWindowIds.Console);
            return;
        }
        _docking.Show(StandardWindowIds.Console);
        ActivateToolContent(StandardWindowIds.Console);
    }

    internal void ActivateToolContent(string id)
    {
        if (_docking.FindContent(id) is IActivatableToolContent activatable)
            activatable.ActivateContent();
    }

    private async Task ShowCommandCatalogForCompletionAsync()
    {
        try
        {
            var result = await _bus.ExecuteAsync(
                $"aurora.ui.show name={StandardWindowIds.Mcp}",
                "UI");
            if (!result.Success)
                _log.Error("console", $"切换命令集失败: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("console", $"切换命令集异常: {ex.GetType().Name}");
        }
    }

    private void OnDockDoubleClick(object sender, MouseButtonEventArgs e)
        => _topBar.HandleDockTabMouseLeftButtonDown(e);

    // ---------------------------------------------------------------- 顶栏状态(UI-05)

    /// <summary>UI-05.3:原状态栏错误计数,点击行为不变(聚焦控制台并只看错误)。</summary>
    private void UpdateErrorBadge()
    {
        ErrorBadge.Content = $"错误 {_errorCount}";
        ErrorBadge.Visibility = _errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnErrorBadgeClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("aurora.log.focus errors=true", "UI");

    // ---------------------------------------------------------------- 顶栏窗口控件(UI-02)

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("aurora.app.window state=minimized", "UI");

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("aurora.app.window state=toggle", "UI");

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.app.hide", "UI");

    internal CommandResult SetFloatingWindowState(string id, string state)
        => _topBar.SetFloatingWindowState(id, state);

    /// <summary>窗体最大化图标切换 + WindowChrome 溢出补偿(UI-02.5)。</summary>
    private void ApplyWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (TryFindResource(maximized ? "Aurora.Icon.Restore" : "Aurora.Icon.Maximize") is Geometry icon)
            MaximizeIcon.Data = icon;
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";

        // 接管非客户区后,最大化的窗体会按可调整边框宽度溢出工作区,
        // 不补偿则顶栏被裁掉一截。
        RootBorder.Padding = _customChrome && maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }

    /// <summary>
    /// 热重载与宿主退出用：绕过「关闭即隐藏」，真正关掉窗口。
    /// </summary>
    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnShellClosing(object? sender, CancelEventArgs e)
    {
        if (_config.CloseBehavior == ShellCloseBehavior.Hide && !_allowClose)
        {
            e.Cancel = true;
            _ = _bus.ExecuteAsync("vulcan.app.hide", "UI");
            _closing = false;
            return;
        }

        _closing = true;
        _chromeUpkeep.Stop();
        SaveWindowBounds();
        _docking.SaveCurrentLayout();

        // Shell 自建的能力由 Shell 自己收尾：模块宿主握着文件监听与防抖定时器，
        // 必须在退出前释放。网关一项随 MCP 迁出 Aurora（Vulcan 4.4.0）而消失。
        _history.Save();
        _catalogSession.Dispose();
    }

    private void OnShellClosed(object? sender, EventArgs e)
        => _topBar.Dispose();

    private void RegisterFrontendLifecycleCommands(CommandRegistry registry)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.hide",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "隐藏 HistoryVulcan 前端窗口并保持后台连接",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Hide();
                return CommandResult.Ok("前端窗口已隐藏");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.focusconsole",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示窗口、打开并聚焦控制台，同时最大化控制台",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                FocusConsole(resetFilters: false, preserveMaximizedLayout: false);
                _docking.MaximizeWindow(StandardWindowIds.Console);
                _console.FocusInput();
                return CommandResult.Ok("控制台已显示、聚焦并最大化");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.show",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示并激活 HistoryVulcan 前端窗口",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                return CommandResult.Ok("前端窗口已显示");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.close",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "退出 HistoryVulcan 前端进程",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                _allowClose = true;
                Close();
                return CommandResult.Ok("前端正在退出");
            }),
        }, FrontendCommandCatalog.Source);
    }

    // ---------------------------------------------------------------- 主窗体边界持久化

    private sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

    private string WindowBoundsPath => Path.Combine(_dataDirectory, "window.json");

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowBoundsPath))
                return;

            var b = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(WindowBoundsPath));
            if (b == null || b.Width < 200 || b.Height < 150)
                return;

            // 粗校验落点仍在虚拟屏幕范围内(多显示器拔除后不至于跑到屏外)
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;
            if (b.Left + b.Width < vLeft + 50 || b.Left > vRight - 50 ||
                b.Top < vTop - 10 || b.Top > vBottom - 50)
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
            if (b.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"恢复主窗体位置失败: {ex.Message}");
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var r = RestoreBounds; // 最大化时记录还原后的边界
            var b = new WindowBounds(r.Left, r.Top, r.Width, r.Height,
                WindowState == WindowState.Maximized);
            File.WriteAllText(WindowBoundsPath, JsonSerializer.Serialize(b));
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"保存主窗体位置失败: {ex.Message}");
        }
    }
}
