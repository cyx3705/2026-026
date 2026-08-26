using System.Text;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Selection;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell;

internal static partial class BuiltinCommands
{
    /// <summary>
    /// 选择通道台账的查询口（REQ-UI-041）。
    ///
    /// 与 <c>aurora.ui.actions</c> 同一条思路：接线是声明出来的，因此「接没接上」必须可查，
    /// 而不是靠盯着界面猜。通道这一侧的失败形态尤其难认——按钮永远灰着，
    /// 与「还没选中」在界面上完全一样，只有台账能把两者分开。
    /// </summary>
    private static void RegisterChannels(CommandRegistry r, SelectionChannels channels)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.channels",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出选择通道、当前选中行与断链引用",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var states = channels.Snapshot();
                var dangling = channels.Dangling;

                if (states.Count == 0 && dangling.Count == 0)
                    return CommandResult.Ok("(没有任何表格声明选择通道)");

                var sb = new StringBuilder($"共 {states.Count} 条选择通道:");
                foreach (var state in states)
                {
                    sb.Append($"\n  {state.Channel}");
                    sb.Append($"\n      声明: {state.Origin}");
                    sb.Append($"\n      选中: {(state.HasSelection ? "有" : "无")}");
                    sb.Append($"\n      引用: {(state.ReferencedBy.Count == 0 ? "(无人引用)" : string.Join(" / ", state.ReferencedBy))}");
                }

                if (dangling.Count > 0)
                {
                    sb.Append($"\n\n{dangling.Count} 条断链引用:");
                    foreach (var reason in dangling)
                        sb.Append("\n  " + reason);
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });
    }

    /// <summary>
    /// 显式重取页面数据（REQ-UI-044）。
    ///
    /// 取数参数引用了选中行的表格会自己跟着通道走，不需要这条；这条是给
    /// 「数据在界面之外被改了」准备的——规则清单、GitHub 状态这类，
    /// 界面这边没有任何信号知道它变了。
    /// </summary>
    private static void RegisterDataRefresh(CommandRegistry r, PageDataRefresher refresher)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.refreshdata",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "重新拉取页面数据（可按页或按节点）",
            Example = "aurora.ui.refreshdata page=rules",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "page", Description = "只刷这一页；省略则全部", Position = 0 },
                new ParameterSpec { Name = "node", Description = "只刷这一个节点 id", Position = 1 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var page = ctx.GetString("page");
                var node = ctx.GetString("node");
                var count = refresher.Refresh(
                    string.IsNullOrWhiteSpace(page) ? null : page,
                    string.IsNullOrWhiteSpace(node) ? null : node);

                if (count > 0)
                    return CommandResult.Ok($"已重新拉取 {count} 处页面数据");

                // 「刷新了 0 处」必须与「刷新完成」分开说：按钮点了没反应时，
                // 差别就在这一句上——是没有这一页，还是这一页本来就没有取数节点。
                var known = refresher.Snapshot();
                return CommandResult.Fail(
                    "没有匹配的取数节点。当前共 " + known.Count + " 处"
                    + (known.Count == 0
                        ? ""
                        : ": " + string.Join(" / ", known.Select(b => b.Page + "#" + b.Node))));
            }),
        });
    }
}
