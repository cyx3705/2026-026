namespace HistoryAurora.Shell.Base.Dialogs;

/// <summary>
/// 宿主确认（§5.2 拦截器；手输指令路径的危险操作闸口）落到主窗体的弹窗层。
/// 任意线程可调用：<see cref="AuroraDialogHost.Show"/> 自己编组到界面线程并等结果。
///
/// 1.30.0 前叫 <c>MessageBoxConfirmation</c>，弹的是独立的 <c>AuroraDialogWindow</c>（REQ-UI-136 删除）。
/// </summary>
public sealed class DialogConfirmation
{
    private readonly AuroraDialogHost _dialogs;

    public DialogConfirmation(AuroraDialogHost dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
    }

    public bool Confirm(string prompt)
    {
        var result = _dialogs.Show(new AuroraDialogRequest
        {
            Kind = AuroraDialogKind.Confirm,
            Title = "需要确认",
            Body = prompt,
            PrimaryText = "确定",
            CancelText = "取消",
            Danger = true,
            DefaultCancel = true,
        });
        return result.Accepted;
    }
}
