namespace HistoryAurora.Shell;

internal partial class ShellWindow
{
    /// <summary>
    /// 自持页面的动作声明。
    ///
    /// **必须在 <see cref="RegisterHostedPages"/> 之前调用。** 页面在建的时候就按 id 解析
    /// 动作（<c>PageRenderer</c> → <c>ActionRegistry.Resolve</c>），台账晚一步，
    /// 页面上每个按钮都会渲染成「未声明的动作」——那不是异常，是画出来的一排警示牌，
    /// 因此不会有任何堆栈告诉你顺序反了。1.8.9 真机上就是这个形态，查了两轮。
    ///
    /// 界面自带的动作走 <see cref="Actions.ActionRegistry.DeclareLocal"/> 而不是
    /// <c>&lt;域&gt;.ui.actions</c>：拉取协议按命令名反推模块域，界面自带的页面不该
    /// 把自己伪装成模块（与 REQ-UI-020 同一条理由）。它们因此也不会被模块重载清掉。
    /// </summary>
    private void DeclareHostedPageActions()
    {
        _actions.DeclareLocal(
            Views.ComponentGalleryCommands.Owner,
            Views.ComponentGalleryCommands.Actions);
        _actions.DeclareLocal(
            Views.HostedPageDescriptions.Owner,
            Views.HostedPageData.Actions);
    }
}
