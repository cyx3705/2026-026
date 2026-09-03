using System.Windows.Threading;
using HistoryAurora.Shell.Components.Modules;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// HistoryAurora 的模块装配入口。
///
/// 5.0 起宿主只注入总线与指令登记口，不再编排 CreateUi / DestroyUi，
/// 也不再向其余模块转发 IShellUiProvider。界面在 Attach 里启动，
/// 卸载走 <see cref="IDisposable"/>。
/// </summary>
public sealed class AuroraBusinessComposition : IModuleContextAware, IDisposable
{
    private IModuleContext? _context;
    private SynchronizationContext? _previousHostUiContext;
    private bool _disposed;

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    public void Attach(IModuleContext context)
    {
        _context = context;
        _previousHostUiContext = context.Bus.UiContext;
        AuroraShellHost.EnsureStarted(context, ReadyTimeout);

        // The host bus normally runs on the service loop. UI-annotated module
        // commands (such as Minerva's pane factory) must create WPF objects on
        // Aurora's STA dispatcher instead.
        if (AuroraShellHost.Window is { } window)
            context.Bus.UiContext = new DispatcherSynchronizationContext(window.Dispatcher);

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
        if (_context != null)
        {
            _context.Bus.FrontendExecutor = null;
            _context.Bus.UiContext = _previousHostUiContext;
        }
        AuroraShellHost.Shutdown(log: null);
    }
}
