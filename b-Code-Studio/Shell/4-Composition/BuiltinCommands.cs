using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.HostedPages.Console;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Composition;

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

    /// <summary>选择通道台账;null 时 aurora.ui.channels 不注册。</summary>
    public Components.Selection.SelectionChannels? Channels { get; init; }

    /// <summary>取数刷新台账;null 时 aurora.ui.refreshdata 不注册。</summary>
    public Components.Pages.PageDataRefresher? DataRefresher { get; init; }

    /// <summary>页面注册协议的拉取器;null 时 aurora.ui.reloadpages 等指令组不注册。</summary>
    public ModulePageLoader? PageLoader { get; init; }

    /// <summary>组件申请台账;与 PageLoader 同进同出。</summary>
    public ComponentRequestStore? ComponentRequests { get; init; }

    /// <summary>
    /// 命令目录会话，供自持页面的 aurora.ui.data 取数。
    ///
    /// 为 null 时取数返回**空表而不是失败**：目录快照那一路
    /// （<see cref="FrontendCommandCatalog"/>）只取描述符、从不执行处理器，
    /// 在那里把它做成必填只会逼出一个假实例。
    /// </summary>
    public Neutral.CommandSurface.LocalCommandCatalogSession? Catalog { get; init; }

    /// <summary>场景（REQ-UI-084）；null 时 aurora.scene.* 照样登记，执行时报「场景未启用」。</summary>
    public Components.Scenes.SceneManager? Scenes { get; init; }
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
        RegisterScenes(r, s);
        RegisterDialog(r, s);
        // 组件测试页的取数指令必须在这里就登记好。
        //
        // 界面总线默认把命令**发给宿主**（AuroraShellHost.WireBuses：只有本机已登记
        // 且 RequiresUiThread 的才留在界面侧），而宿主注册表里只有 Attach 那一刻
        // PublishShellCommands 抄过去的那一批。晚于 Attach 登记的界面命令，
        // 在宿主那边永远不存在——症状就是一条 `✗ 未知指令: aurora.preview.rows`。
        // 1.8.10 把这批指令挪到"页面打开前登记"，正是踩了这条不变量。
        HostedPages.Views.ComponentGalleryCommands.Register(r);
        // 自持页面的取数与组合指令同理，而且更要紧：命令集、指令详情、模块管理三页
        // 的全部数据都从 aurora.ui.data 来，它进不了宿主注册表就是三页一起空白。
        HostedPages.Views.HostedPageData.Register(r, new HostedPages.Views.HostedPageData.Sources
        {
            Bus = () => s.Bus,
            Catalog = () => s.Catalog,
        });
        if (s.Panels != null)
            RegisterPanel(r, s, s.Panels);
        if (s.Actions != null)
            RegisterActions(r, s, s.Actions);
        if (s.Channels != null)
            RegisterChannels(r, s.Channels);
        if (s.DataRefresher != null)
            RegisterDataRefresh(r, s.DataRefresher);
        if (s.PageLoader != null && s.ComponentRequests != null)
            RegisterPages(r, s.PageLoader, s.ComponentRequests);
        // 就绪钩子无条件登记：它只调 ShellWindow 的整轮发现，不依赖上面任何一个可选台账。
        // 更要紧的是它必须在 Attach 那一刻就进宿主注册表——晚登记的界面命令宿主永远看不见，
        // 而宿主正是靠「注册表里有没有这条」决定要不要通知本模块。
        RegisterHostReady(r, s);
    }
}
