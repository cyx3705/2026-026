using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Web;
using HistoryAurora.Shell;

namespace HistoryAurora;

/// <summary>
/// HistoryAurora 前端应用（REQ-A7，DEC-004）。
///
/// 从 HistoryVulcan.App 的前端半边迁入。与迁出前的区别只有身份与依赖方向：
/// 本进程**不再承载服务**——没有 `--service` 分支、不组装 ServiceComposer、
/// 不监听任何端口，只作为 IPC 客户端连到宿主。宿主已无头化（REQ-A4）。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private ShellServiceClient? _serviceClient;
    private Mutex? _frontendMutex;

    /// <summary>
    /// 单实例互斥锁。必须与宿主的前端锁**不同名**：迁出后 Aurora 与宿主是两个产品，
    /// 同名会让旧宿主前端（若还装着）与 Aurora 互相把对方判为"已在运行"而拒绝启动，
    /// 且症状是"点了图标没反应"，很难联想到锁名。
    /// </summary>
    private const string FrontendMutexName = "Local\\OneHistory.HistoryAurora.Frontend";

    /// <summary>诊断指令开关(DEC-023):默认关闭,正式命令集不含承压注水等诊断工具。</summary>
    private const string DiagnosticCommandsSettingKey = "diagnostics.commands";

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _frontendMutex = new Mutex(initiallyOwned: true, FrontendMutexName, out var created);
        if (!created)
        {
            ActivateExistingFrontend();
            Shutdown();
            return;
        }

        AppIdentity.Use(typeof(App).Assembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;
        RemoveLegacyDemoPanel(paths, log);

        // N-05:全局未处理异常捕获 → 落日志并由 Shell 自动打开控制台,不弹错误框
        DispatcherUnhandledException += (_, args) =>
        {
            // AvalonDock can raise a stale visual-tree mouse-leave exception while
            // a pane template is replaced during focus/maximize. It is harmless
            // after the layout has been rebuilt, but must not look like an HistoryVulcan
            // fatal error in the console.
            if (IsTransientAvalonDockMouseLeave(args.Exception))
            {
                log.Log(ShellLogLevel.Debug, "shell.chrome", "Ignored transient AvalonDock mouse-leave exception during pane rebuild");
                args.Handled = true;
                return;
            }

            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");

        var config = CreateStandaloneFrontendConfig(identity);
        config.ModuleDirectory = paths.ModulesDir;

        // 默认布局保留控制台底部停靠位置；业务页面由模块提供。
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
            // 控制台内容由 Shell 提供(§4.4);此处只声明停靠位置
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.Modules,
            Title = "模块管理",
            // 左侧：右侧默认不再放页面。宽度与其他左侧页取同一个 0.38，
            // 否则左栏宽度会取决于哪个模块最后装载。
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.38,
            // 内容由 Shell 的 ModulesView 接管；这里只声明独立宿主的默认位置。
        });
        // 承压注水是诊断工具,不属于正式命令集(DEC-023):默认不注册,
        // 只有显式把 diagnostics.commands 置为 true 的宿主才登记。
        // 它同时是异步长任务 + Progress 上报的示范(§5.2)与验收 8 / N-03 的承压入口,
        // 因此保留能力而不是删除。
        if (bool.TryParse(settings.Get(DiagnosticCommandsSettingKey), out var diagnostics) && diagnostics)
        {
            config.ConfigureCommands = registry =>
            {
                registry.Register(BuildLogFloodCommand(log));
            };
            log.Warn("app", $"诊断指令已启用({DiagnosticCommandsSettingKey}=true):vulcan.log.flood 已注册。");
        }

        var window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var executablePath = ResolveExecutablePath();
        // REQ-A7：端点文件属于**宿主**的数据根，不是 Aurora 的。
        //
        // 迁出前前端与服务共用一个身份（HistoryVulcan），`paths.Root` 恰好同时是两者的根，
        // 这行代码因此看起来是对的。切分后 Aurora 的身份变成 HistoryAurora，
        // 同样的写法会去 %AppData%\HistoryAurora\service\ 找一个永远不存在的文件——
        // 而失败方式是**静默降级**：连不上就转本地模式，用户只看到"某些命令不见了"。
        // 因此这里显式按宿主身份取根，不跟随自身身份。
        var endpointPath = Path.Combine(HostServiceEndpointDirectory(), "endpoint.json");
        var service = ConnectToService(endpointPath, executablePath, log);
        if (service != null)
        {
            _serviceClient = service;
            service.LogReceived += (_, entry) => window.AddTransientLog(entry);
            service.ModuleRevisionReceived += revision =>
            {
                _ = ReloadUiModulesFromServiceAsync(service, window.Modules, log);
            };
            window.Commands.RemoteExecutor = service.ExecuteAsync;
            window.Commands.ShouldUseRemoteCommand = (text, source) =>
            {
                if (source.Equals("Service:Relay", StringComparison.OrdinalIgnoreCase))
                    return false;

                // 本机已登记且需 UI 线程的页面状态命令（如 HistoryMinerva.convert）就地执行，
                // 勿转到服务进程——那边没有页面实例。
                try
                {
                    var parsed = CommandParser.Parse(text.Trim());
                    if (IsBackendMcpSettingCommand(parsed))
                        return true;
                    if (parsed.Name.StartsWith("vulcan.app.", StringComparison.OrdinalIgnoreCase))
                        return false;
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
            _ = service.RunEventLoopAsync(window.Commands);
            _ = ReloadUiModulesFromServiceAsync(service, window.Modules, log);
        }
        window.Show();

        log.Info("app", $"{identity.Name} {identity.Version} 启动完成,数据目录: {paths.Root}");

        // --exec "指令":启动后顺序执行(自动化/自测入口)
        var startupCommands = new List<string>();
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] != "--exec")
                continue;
            if (i + 1 >= e.Args.Length)
            {
                log.Warn("app", "启动参数 --exec 缺少后续指令，已忽略");
                break;
            }
            startupCommands.Add(e.Args[++i]);
        }

        if (e.Args.Any(arg => arg.Equals("--focus-console", StringComparison.OrdinalIgnoreCase)))
        {
            startupCommands.Add("vulcan.app.focusconsole");
        }

        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    private static async Task RunStartupCommandsAsync(ShellWindow window, List<string> commands)
    {
        foreach (var command in commands)
            await window.Commands.ExecuteAsync(command, "脚本:startup");
    }

    internal static ShellConfig CreateStandaloneFrontendConfig(
        HistoryVulcan.Core.ApplicationIdentity identity)
        => new()
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            EnableModules = false,
            EnableUiModules = true,
            // 独立双进程宿主的 MCP 由后台权威注册表统一承载；前端只保留远程管理视图。
            EnableMcp = false,
            RequireConfirmedModuleSources = true,
            EnableRemoteManagementViews = true,
            CloseBehavior = ShellCloseBehavior.Hide,
            // HistoryVulcan 独立宿主是模块生命周期的最终所有者；Janus 等产品只声明
            // 自己的业务窗口与模块，不再包装第二套 ModuleHost/ModulesView。
        };

    internal static bool IsBackendMcpSettingCommand(ParsedCommand parsed)
    {
        if (!parsed.Name.Equals("vulcan.app.get", StringComparison.OrdinalIgnoreCase)
            && !parsed.Name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase))
            return false;

        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        return key?.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task ReloadUiModulesFromServiceAsync(
        ShellServiceClient service,
        ModuleHost? host,
        IShellLog log)
    {
        if (host == null)
            return;
        var result = await service.ExecuteAsync("vulcan.module.list", "UI", CancellationToken.None)
            .ConfigureAwait(false);
        IReadOnlyList<ModuleMeta>? modules = result.Data switch
        {
            IReadOnlyList<ModuleMeta> typed => typed,
            JsonElement element => JsonSerializer.Deserialize<List<ModuleMeta>>(
                element.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
            _ => null,
        };
        if (!result.Success || modules == null)
        {
            log.Warn("module", "无法取得后台确认的模块来源，前端 UI 模块保持原快照");
            return;
        }

        var manifests = modules
            .Select(module => module.ManifestPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        await Task.Run(() => host.ReloadConfirmedSources(manifests)).ConfigureAwait(false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceClient?.Dispose();
        _log?.Dispose(); // 冲刷文件写入队列
        try { _frontendMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _frontendMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsTransientAvalonDockMouseLeave(Exception exception)
    {
        if (exception is not NullReferenceException)
            return false;

        return exception.StackTrace?.Contains(
            "AvalonDock.Controls.AnchorablePaneTabPanel.OnMouseLeave",
            StringComparison.Ordinal) == true;
    }

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetExtension(processPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return processPath;
        return Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
    }

    /// <summary>
    /// 宿主写 <c>endpoint.json</c> 的目录：<c>%AppData%\HistoryVulcan\service</c>。
    ///
    /// 宿主身份名在此写死。它不是配置项——Aurora 连的就是 HistoryVulcan 这一个宿主，
    /// 做成可配置只会让"连错了后台"变成一种可能，而那种故障同样是静默的。
    /// 宿主若改名，这里必须同步改，编译期发现不了。
    /// </summary>
    private const string HostIdentityName = "HistoryVulcan";

    private static string HostServiceEndpointDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            HostIdentityName,
            "service");

    private static ShellServiceClient? ConnectToService(
        string endpointPath,
        string executablePath,
        IShellLog log)
    {
        try
        {
            var endpoint = ReadEndpoint(endpointPath);
            if (endpoint is { Port: > 0 })
            {
                var existing = CreateServiceClient(endpoint, endpointPath);
                if (existing.WaitForReadyAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult())
                    return existing;
                existing.Dispose();
            }

            // REQ-A7：Aurora 不再派生后台。迁出前前端与服务是同一个 exe 的两个角色，
            // 连不上就用 `--service` 把自己再拉起来一份；现在宿主是**另一个产品**
            // （HistoryVulcan.exe），Aurora 既不知道它装在哪，也不应替它决定何时启动。
            //
            // 宿主由 `vulcan.svc.autostart` 注册为登录启动，正常情况下先于 Aurora 就绪；
            // 未就绪时如实报告并以本地模式继续，而不是去猜一个可执行文件路径。
            // 猜错的后果是静默降级——用户只会看到"某些命令不见了"，却不知道后台没连上。
            log.Warn("service", "后台服务未就绪（宿主未运行或端点失效），Aurora 以本地模式继续运行");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            log.Warn("service", $"后台连接失败，Aurora 以本地模式继续运行: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 端点必须同时给出端口和凭据；缺一都连不上后台（宿主 DEC-046 起要求持券）。
    ///
    /// 派生服务后的等待循环用它判断"记录是否已刷新"。只判 <c>null</c> 是不够的：
    /// 服务被强杀时来不及删除 endpoint.json，残留记录端口有效、accessToken 为空，
    /// 会让循环第一轮就误判为就绪。
    /// </summary>
    internal static bool IsUsableEndpoint(ServiceEndpoint? endpoint)
        => endpoint is { Port: > 0 } && !string.IsNullOrEmpty(endpoint.AccessToken);

    private static ShellServiceClient CreateServiceClient(ServiceEndpoint endpoint, string endpointPath)
    {
        // 凭据每次调用时从 endpoint.json 现取，不捕获快照：后台重启会换发新令牌并重写该文件，
        // 而客户端的重连是长期存活的。捕获初次读到的值会让前端在后台重启后静默 401。
        // 文件读不到时回退到初次值，避免瞬时 IO 抖动把一个本来有效的会话打掉。
        var initial = endpoint.AccessToken;
        var profile = new ShellEndpointProfile(
            new Uri($"http://127.0.0.1:{endpoint.Port}/"),
            Guid.NewGuid().ToString("N"),
            AccessTokenProvider: () => ReadEndpoint(endpointPath)?.AccessToken ?? initial,
            ServerId: endpoint.ServerId,
            ConnectTimeout: TimeSpan.FromSeconds(5));
        // 客户端名决定服务侧注册表里的来源标签（frontend:<name>）。
        // REQ-A7 验收要求它变成 HistoryAurora——来源标签是判断"这条命令由谁实现"的
        // 唯一运行时凭据，留着旧名会让迁移在目录上完全看不出来。
        return new ShellServiceClient(profile, "HistoryAurora");
    }

    private static ServiceEndpoint? ReadEndpoint(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ServiceEndpoint>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void ActivateExistingFrontend()
    {
        try
        {
            var current = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("HistoryVulcan"))
            {
                try
                {
                    if (process.Id == current || process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    ShowWindowAsync(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // A duplicate launch must never surface a dialog or crash the existing frontend.
        }
    }

    internal sealed record ServiceEndpoint(
        int Port,
        string ServerId,
        int ProcessId,
        string? AccessToken = null);

    private static void RemoveLegacyDemoPanel(AppPaths paths, IShellLog log)
    {
        var legacyPanel = Path.Combine(paths.PanelsDir, "motor.json");
        try
        {
            if (!File.Exists(legacyPanel))
                return;
            File.Delete(legacyPanel);
            log.Info("migration", $"已删除旧版演示电机面板: {legacyPanel}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("migration", $"删除旧版演示电机面板失败: {ex.Message}");
        }
    }

    /// <summary>
    /// vulcan.log.flood:按指定速率注入日志(验收 8 / N-03 承压验证)。
    /// 异步长任务示范:后台线程产出、经 Progress 上报进度、全程不阻塞 UI(§5.2)。
    /// </summary>
    private static CommandDescriptor BuildLogFloodCommand(ShellLog log) => new()
    {
        Name = "vulcan.log.flood",
        Domain = "vulcan",
        CommandClass = "log",
        Summary = "诊断:日志承压测试,按指定速率注入日志",
        Example = "vulcan.log.flood rate=1000 seconds=30",
        // 最高 100000 条/秒 × 600 秒;误触会淹没控制台与日志文件,故走确认闸口。
        // MCP/Web 侧另由 McpExposurePolicy 硬排除,远程不可达。
        Dangerous = true,
        ConfirmPrompt = ctx =>
            $"确认注入日志 {ctx.GetInt("rate", 1000)} 条/秒 × {ctx.GetInt("seconds", 30)} 秒?",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "rate",
                Description = "每秒注入条数",
                Type = ParamType.Int,
                Default = "1000",
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "seconds",
                Description = "持续秒数",
                Type = ParamType.Int,
                Default = "30",
                Position = 1,
            },
        ],
        Handler = async ctx =>
        {
            var rate = Math.Clamp(ctx.GetInt("rate", 1000), 1, 100_000);
            var seconds = Math.Clamp(ctx.GetInt("seconds", 30), 1, 600);
            var total = 0L;
            var sw = Stopwatch.StartNew();

            await Task.Run(async () =>
            {
                // 每 100ms 一批;按“目标累计 = 速率 × 已流逝时间”补齐,睡眠误差不累积
                var lastProgress = 0L;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    var target = Math.Min(
                        (long)(sw.Elapsed.TotalSeconds * rate),
                        (long)rate * seconds);
                    while (total < target)
                    {
                        total++;
                        log.Log(ShellLogLevel.Debug, "flood",
                            $"承压测试消息 #{total} @{sw.ElapsedMilliseconds}ms");
                    }

                    if (sw.ElapsedMilliseconds - lastProgress >= 5000)
                    {
                        lastProgress = sw.ElapsedMilliseconds;
                        ctx.Progress?.Report($"{sw.Elapsed.TotalSeconds:0}s / {seconds}s,已注入 {total} 条");
                    }

                    await Task.Delay(100);
                }

                // 补齐尾差
                for (var expected = (long)rate * seconds; total < expected; total++)
                {
                    log.Log(ShellLogLevel.Debug, "flood",
                        $"承压测试消息 #{total + 1} @{sw.ElapsedMilliseconds}ms");
                }
            });

            return CommandResult.Ok(
                $"承压完成:{total} 条 / {sw.Elapsed.TotalSeconds:0.0}s,实际速率 {total / sw.Elapsed.TotalSeconds:0} 条/秒");
        },
    };

    // ---------------------------------------------------------------- 演示面板与指令(M4)

}
