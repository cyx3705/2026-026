using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services;
using HistoryAurora.Shell.Console;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Panels;

namespace HistoryAurora.Shell;

/// <summary>内置指令组的依赖集(注册时一次性提供)。</summary>
internal sealed class ShellCommandServices
{
    public required ShellWindow Window { get; init; }

    public required IDockingService Docking { get; init; }

    public required ConsoleView Console { get; init; }

    public required CommandHistory History { get; init; }

    public required ISettingsService Settings { get; init; }

    public required IShellLog Log { get; init; }

    public required CommandBus Bus { get; init; }

    public required string DataDirectory { get; init; }

    /// <summary>控制窗口群管理器;null 时 panel.* 指令组不注册。</summary>
    public PanelManager? Panels { get; init; }

    /// <summary>动作声明台账;null 时 aurora.ui.actions 指令组不注册。</summary>
    public ActionRegistry? Actions { get; init; }

    /// <summary>页面注册协议的拉取器;null 时 aurora.ui.reloadpages 等指令组不注册。</summary>
    public ModulePageLoader? PageLoader { get; init; }

    /// <summary>组件申请台账;与 PageLoader 同进同出。</summary>
    public ComponentRequestStore? ComponentRequests { get; init; }
}
/// <summary>
/// 框架内置指令组(§5.3 / 附录 B):help / history / run /
/// app.* / log.* / win.* / layout.* 与 panel.*；cls 仅为 aurora.log.clear 的兼容别名。
/// </summary>
internal static partial class BuiltinCommands
{
    public static void Register(CommandRegistry r, ShellCommandServices s)
    {
        RegisterBasics(r, s);
        RegisterApp(r, s);
        RegisterLog(r, s);
        RegisterWin(r, s);
        RegisterLayout(r, s);
        RegisterDialog(r, s);
        RegisterComponentGallery(r);
        if (s.Panels != null)
            RegisterPanel(r, s, s.Panels);
        if (s.Actions != null)
            RegisterActions(r, s, s.Actions);
        if (s.PageLoader != null && s.ComponentRequests != null)
            RegisterPages(r, s.PageLoader, s.ComponentRequests);
    }
}
