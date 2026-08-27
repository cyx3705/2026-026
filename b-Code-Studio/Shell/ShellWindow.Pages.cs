using HistoryAurora.Shell.Docking;
using HistoryAurora.Shell.Pages;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell;

/// <summary>
/// Aurora 自持页面的装配（REQ-UI-052）。**界面用自己的协议描述自己的页面。**
/// </summary>
internal partial class ShellWindow
{
    /// <summary>
    /// 把自持页面的描述建成窗格内容，并按 Id 并进 <c>ToolWindows</c>。
    ///
    /// **为什么在构造期同步做，而不是交给 <see cref="ModulePageLoader"/>：**
    ///
    /// 两条原因，任何一条单独成立都足够：
    ///
    /// 一是**公开契约**。派生应用按 Id 声明 <c>ToolWindows</c>，界面按 Id 合并
    /// （<see cref="TakeOverDescriptor"/>）。走拉取器的 <c>RegisterWindow</c> 会绕过这次合并，
    /// 派生应用声明的位置、比例与标题**静默失效**——而这类失效在本仓的历史里
    /// 从来不是当场发现的。
    ///
    /// 二是**时序**。命令集（<c>mcp</c>）是中央主文档，停靠层在 <c>Initialize</c> 时就要
    /// 按它修复中央工作区（<c>EnsureCentralWorkspace</c>）；它若要等第一轮异步发现才出现，
    /// 启动那一刻的中央区是空的。自持页的描述是编译进来的常量，**没有任何理由等一轮往返**。
    ///
    /// 与模块页共用的是真正会长歪的那一段：渲染、包边、裁切，全在
    /// <see cref="PageRegistrar.Build"/> 里（REQ-UI-051）。
    /// </summary>
    private void RegisterHostedPages()
    {
        var parsed = PageDescriptionReader.Read(
            Views.HostedPageDescriptions.Json,
            Views.HostedPageDescriptions.Owner);

        if (!parsed.Ok)
        {
            // 自持页描述是本仓自己的常量，坏掉属于自身缺陷而不是模块的锅，因此报 Error。
            // 但仍然只是少几页，不能把整个界面拖垮——启动路径上不抛。
            _log.Log(ShellLogLevel.Error, "page", "自持页面描述无效: " + parsed.Error);
            return;
        }

        foreach (var page in parsed.Value!.Pages)
        {
            // 模块管理页归开关管。**跳过整页而不是把描述改空**：一页在不在，
            // 与它长什么样是两件事，混在一起的话「关掉模块管理」会变成
            // 「有一页模块管理，但里面什么都没有」。
            if (page.Id.Equals(StandardWindowIds.Modules, StringComparison.OrdinalIgnoreCase)
                && !_hostedModulesPage)
                continue;

            var built = _hostedPages.Build(Views.HostedPageDescriptions.Owner, page);
            if (built.Root is not { } content)
                continue;

            // 缺件必须出账。自持页出现缺件是**最该被看见**的一种：
            // 它说明界面自己的协议表达不了界面自己的页面，而那正是这套协议存在的理由。
            foreach (var component in built.Missing)
            {
                _log.Log(ShellLogLevel.Warn, "page",
                    $"自持页 {page.Id} 引用了未提供的组件: {component}");
                _componentRequests?.Record(
                    component,
                    Views.HostedPageDescriptions.Owner,
                    Views.HostedPageDescriptions.Owner + "/" + page.Id,
                    "界面自持页面");
            }

            TakeOverDescriptor(
                page.Id,
                page.Title,
                PageRegistrar.ParseSide(page.Placement.Side),
                PageRegistrar.Clamp(page.Placement.Ratio),
                () => content,
                forcePlacement: false,
                fallbackVisible: page.Placement.Visible,
                fallbackSingleton: page.Placement.Singleton);
        }

        BridgeCommandSelection();
    }

    /// <summary>命令集页把选中行发到这个通道；详情页与动作都按它取值。</summary>
    internal const string CommandChannel = "aurora.mcp.command";

    /// <summary>
    /// 把命令集的选中行接回 <c>CommandSelectionState</c>。
    ///
    /// **这是一条公开契约，不能随视图一起消失。** <c>IShellCommandWorkbenchHost.CommandSelection</c>
    /// 是界面向外暴露的「当前选中哪条指令」，1.8.18 之前由 <c>CommandCatalogView</c> 在
    /// 选中变化时写入。那个视图这一版删掉了，若不补这一句，外部消费方读到的永远是空——
    /// **而它不会报错**：属性还在，接口还在，编译照过，只是永远不变。
    ///
    /// 这正是本仓反复吃过的那种亏（<c>web.frontendcatalog</c> 的幽灵目录、
    /// <c>view.filterable</c> 的空转声明）：形状还在、语义没了，没有任何一处会喊疼。
    /// 页面改形态时，**先问谁在读它**，比问它自己怎么画重要。
    /// </summary>
    private void BridgeCommandSelection()
        => _channels.Changed += (_, e) =>
        {
            if (!e.Channel.Equals(CommandChannel, StringComparison.OrdinalIgnoreCase))
                return;

            var name = e.Row != null && e.Row.TryGetValue("name", out var value) ? value : null;
            _commandSelection.CurrentCommandName = string.IsNullOrWhiteSpace(name) ? null : name;

            // 目录会话自己也要跟上：控制台的上下选择与补全都按它的当前项走。
            _catalog.Select(_commandSelection.CurrentCommandName);
        };
}
