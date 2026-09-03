using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Neutral;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 服务端前端代理的框架目录。描述符由真实 <see cref="BuiltinCommands.Register"/>
/// 装配结果投影，名称分类只在此保留一份。
/// </summary>
public static class FrontendCommandCatalog
{
    /// <summary>来源标记的权威定义在中立层（<see cref="FrontendCommandSource"/>），这里只是转发。</summary>
    public const string Source = FrontendCommandSource.Name;

    private static readonly Lazy<CatalogSnapshot> FrameworkSnapshot =
        new(CreateFrameworkSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<CommandDescriptor> FrameworkSourceDescriptors => FrameworkSnapshot.Value.Frontend;

    /// <summary>Actual desktop registrations for metadata shared with a service host.</summary>
    public static IReadOnlyList<CommandDescriptor> SharedBuiltinSourceDescriptors =>
        FrameworkSnapshot.Value.SharedBuiltins;

    // CreateFrameworkProxies / CreateProxy 随进程外前端退役（DEC-008，Vulcan 4.2.0）。
    //
    // 它们把界面指令投影成「代理描述符」，登记到服务端的注册表里，由服务把调用中继回
    // 另一个进程的界面。界面变成宿主内模块之后中继链路本身消失了，代理无处可投——
    // 它们依赖的 Core.FrontendCommandCapability 也已随之从宿主删除。
    // 本类保留下来的是 Source 常量与框架描述符快照：那两样与中继无关。

    private static CatalogSnapshot CreateFrameworkSnapshot()
    {
        var registry = new CommandRegistry();
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = null!,
            Docking = null!,
            Console = null!,
            History = null!,
            Settings = null!,
            Log = null!,
            Bus = null!,
            DataDirectory = "",
            Panels = new PanelManager(),
            // 目录快照只取描述符,处理器一律不执行,因此依赖可以是空的。
            Actions = new ActionRegistry(null!, null!),
        });

        var all = registry.All();
        var frontend = all
            .Where(descriptor => registry.GetSource(descriptor.Name)
                .Equals(Source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sharedBuiltins = all
            .Where(descriptor => BuiltinCommandDefinitions.Contains(descriptor.Name))
            .ToList();
        return new CatalogSnapshot(frontend, sharedBuiltins);
    }

    private sealed record CatalogSnapshot(
        IReadOnlyList<CommandDescriptor> Frontend,
        IReadOnlyList<CommandDescriptor> SharedBuiltins);
}
