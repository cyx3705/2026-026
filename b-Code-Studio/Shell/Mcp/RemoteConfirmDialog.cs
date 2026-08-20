using System.Windows;

namespace HistoryAurora.Shell.Mcp;

/// <summary>
/// 宿主确认中继对话框(V2.2 CX-02):MCP 危险指令请求 → 宿主端弹框由人裁决,带倒计时。
/// 独立于总线 Confirmation(即使 --yes 生效也弹真实框),满足 CX-03。
/// 返回 true=允许 / false=拒绝 / null=超时(按拒绝处理)。
/// </summary>
internal static class RemoteConfirmDialog
{
    public static bool? Ask(Window owner, string prompt, int timeoutSeconds)
    {
        var dark = owner is ShellWindow shell && shell.IsDarkTheme;
        var result = AuroraDialogWindow.Show(
            new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Confirm,
                Title = "MCP 远程请求 · 需要确认",
                Body = prompt,
                PrimaryText = "允许执行",
                CancelText = "拒绝",
                Danger = true,
                DefaultCancel = true,
                TimeoutSeconds = timeoutSeconds,
            },
            owner,
            dark);

        if (result.TimedOut)
            return null;
        return result.Accepted;
    }
}
