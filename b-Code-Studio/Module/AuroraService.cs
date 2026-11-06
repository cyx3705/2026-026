using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Neutral.Storage;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// Aurora 2.1.0：装在宿主上的无界面服务。界面只在网页里（REQ-WEB-040/041）。
/// </summary>
/// <remarks>
/// <para>Attach 时做四件事：登记 aurora.* 指令、拉起 Aurora Web 子进程、登记为宿主唯一前端、
/// 起一条不建窗口的 STA 线程交给宿主（别的模块声明 RequiresUiThread 的指令仍需要它建自己的 WPF 窗）。</para>
/// <para>卸载与热装走 <see cref="Dispose"/>：关子进程（关 stdin，3 秒等不到就强杀）、停 STA 线程、退订事件。</para>
/// </remarks>
internal sealed class AuroraService : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly WebProcess _web;
    private readonly MemoryShellLog _log;
    private readonly Thread _uiThread;
    private readonly Dispatcher _dispatcher;
    private IDisposable? _frontend;
    private bool _disposed;

    private AuroraService(IModuleContext context)
    {
        var environment = context.Environment;
        var executable = Path.Combine(environment.PackageDirectory, "web", "HistoryAurora.Web.exe");
        _web = new WebProcess(executable, context.Log);
        (_uiThread, _dispatcher) = StartUiThread();

        // 控制台只显示宿主那一份日志（DEC-042）：Aurora 不另建日志。
        _log = HostLogFeed.Create(context);

        var catalog = ShellCatalog.FromHost(context.Bus);
        _subscriptions.Add(context.Subscribe("vulcan.catalog.changed", _ => catalog.Invalidate()));
        var table = new CommandTable();
        var bus = new ShellBus(context.Bus, catalog, table);
        var paths = AuroraPaths.ForDataDirectory(environment.DataDirectory);
        var settings = new JsonSettingsStore(paths.SettingsFile);

        AuroraCommands.Register(table, new AuroraCommands.Services
        {
            Web = _web,
            Log = _log,
            Bus = bus,
            Catalog = new LocalCommandCatalogSession(bus, _log),
            Requests = new ComponentRequestStore(settings, _log),
            DataDirectory = environment.DataDirectory,
        });
        context.RegisterCommands(registrar =>
        {
            foreach (var descriptor in table.All())
                if (!catalog.TryGet(descriptor.Name, out _))
                    registrar.Register(descriptor);
        });

        // 模块装上、卸下、接入失败都要让模块管理页重取（1.29.1 起的约定，Web 上同样成立）。
        _subscriptions.Add(context.Subscribe("vulcan.module.changed", _ =>
            _web.Broadcast("aurora.ui.refreshdata", new Dictionary<string, string> { ["page"] = "modules" })));

        _frontend = context.RegisterFrontend(new WebFrontend(new DispatcherSynchronizationContext(_dispatcher), _web, context.Log));
        _web.EnsureStarted();
    }

    /// <summary>网页地址。</summary>
    internal string Url => _web.Url;

    /// <summary>装载入口；失败写宿主日志后照常抛给宿主，让它记为接入失败。</summary>
    internal static AuroraService Start(IModuleContext context)
    {
        try
        {
            return new AuroraService(context);
        }
        catch (Exception ex)
        {
            context.Log.Log(ShellLogLevel.Fatal, "aurora", $"Aurora 服务启动失败: {ex}");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _frontend?.Dispose();
        _frontend = null;
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        _log.Dispose();
        _web.Dispose();
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (!ReferenceEquals(Thread.CurrentThread, _uiThread))
            _uiThread.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>不建窗口的 STA 线程；后台线程，不钉住宿主进程。</summary>
    private static (Thread Thread, Dispatcher Dispatcher) StartUiThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            Name = "HistoryAurora.UiContext",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return (thread, dispatcher!);
    }
}

/// <summary>
/// 控制台的宿主日志来源：新纪录订阅 <c>vulcan.log.entry</c>，旧记录执行 <c>vulcan.log.recent</c> 补读。
/// 载荷字段与 <c>vulcan.log.recent</c> 同形：<c>time / level / category / message</c>。
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
