using System.Windows.Threading;
using HistoryAurora.Shell.Base.Dialogs;
using HistoryAurora.Shell.Composition;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// Aurora 经 <see cref="IModuleContext.RegisterFrontend"/> 交给宿主的唯一前端（宿主 5.4）。
/// </summary>
/// <remarks>
/// 取代此前直接改写宿主总线的 FrontendExecutor / Confirmation / ConfirmationRouter / UiContext：
/// 宿主只认这一个登记，Aurora 卸载时由宿主撤销，确认回到宿主缺省的拒绝。
/// </remarks>
internal sealed class AuroraFrontend : IFrontend
{
    private readonly ShellWindow _window;
    private readonly MessageBoxConfirmation _confirmation;

    public AuroraFrontend(ShellWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _confirmation = new MessageBoxConfirmation(window);
        UiContext = new DispatcherSynchronizationContext(window.Dispatcher);
    }

    /// <summary>Aurora 的 STA 界面线程；宿主把 RequiresUiThread 指令编组到这里建 WPF 对象。</summary>
    public SynchronizationContext UiContext { get; }

    public bool Confirm(string prompt) => _confirmation.Confirm(prompt);

    public Task<CommandResult> ExecuteAsync(string commandName, string source, CancellationToken cancellation)
        => _window.Dispatcher
            .InvokeAsync(() => AuroraShellHost.DispatchFrontendCommand(_window, commandName, source, cancellation))
            .Task
            .Unwrap();
}
