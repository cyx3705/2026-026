using System.Windows;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell;

/// <summary>
/// 二次确认的模态对话框实现(§5.2 拦截器;手输指令路径的危险操作闸口)。
/// 编组到 UI 线程弹窗,任意线程可调用。走 Aurora 自持弹窗,不依赖系统 MessageBox。
/// </summary>
public sealed class MessageBoxConfirmation : IConfirmationService
{
    private readonly Window _owner;

    public MessageBoxConfirmation(Window owner) => _owner = owner;

    public bool Confirm(string prompt)
    {
        var dark = _owner is ShellWindow shell && shell.IsDarkTheme;
        var result = AuroraDialogWindow.Show(
            new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Confirm,
                Title = "需要确认",
                Body = prompt,
                PrimaryText = "确定",
                CancelText = "取消",
                Danger = true,
                DefaultCancel = true,
            },
            _owner,
            dark);
        return result.Accepted;
    }
}
