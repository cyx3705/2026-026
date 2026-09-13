using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// HistoryAurora 的模块装配入口。
///
/// 5.0 起宿主只注入总线与指令登记口，不再编排 CreateUi / DestroyUi，
/// 也不再向其余模块转发 IShellUiProvider。界面在 Attach 里启动，
/// 卸载走 <see cref="IDisposable"/>。宿主 5.4 起前端经 RegisterFrontend 登记，不再改写宿主总线。
/// </summary>
public sealed class AuroraBusinessComposition : IModuleContextAware, IDisposable
{
    private IModuleContext? _context;
    private IDisposable? _frontend;
    private bool _disposed;

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    public void Attach(IModuleContext context)
    {
        _context = context;
        AuroraShellHost.EnsureStarted(context, ReadyTimeout);

        // 登记为宿主唯一前端：确认、界面线程（Minerva 窗格工厂等 UI 注解指令要在
        // Aurora 的 STA 线程上建 WPF 对象）与 vulcan.app.* 生命周期中继一次交出。
        if (AuroraShellHost.Window is { } window)
            _frontend = context.RegisterFrontend(new AuroraFrontend(window));

        AuroraShellHost.ShowMainWindowIdle();
    }

    internal IModuleContext? Context => _context;

    internal void RegisterCommands(Action<CommandRegistry> configure)
        => _context?.RegisterCommands(configure);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _frontend?.Dispose();
        _frontend = null;
        AuroraShellHost.Shutdown(log: null);
    }
}
