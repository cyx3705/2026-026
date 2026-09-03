using HistoryAurora.Shell.Components.Pages;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 宿主就绪钩子（宿主 DEC-057）。
///
/// 界面的困境在于：它在自己的 <c>Attach</c> 里就开了 STA 线程，而那一刻宿主还在装别的模块。
/// 于是「什么时候可以去问各模块要页面」这件事，界面自己判断不了——1.16.0 之前靠的是
/// 「注册表安静 150ms」这个猜测。猜测在宿主装载慢的时候必然落空：装载期间注册表本来就
/// 安静得远超 150ms，那一轮发现问到的是一张还没长齐的目录，
/// 表现为启动时有几率少几页、按钮报「未声明」、控制台成片 <c>未知指令: 别的模块域.*</c>。
///
/// 宿主 5.1.3 起把「全部模块都接上了」做成一个确定的时刻，并按命令名通知声明了本钩子的模块。
/// 界面在这里做整轮重拉，猜测退居兜底。
///
/// **对旧宿主保持可用**：5.1.2 及以前不认识这条命令，它就只是注册表里多出的一条命令，
/// 永远不会被调用，防抖那条路径原样保留，行为与 1.16.0 一致。因此本版不抬
/// <c>MinimumHistoryVulcanVersion</c>——界面没有用到任何新的宿主公开面。
/// </summary>
internal static partial class BuiltinCommands
{
    private static void RegisterHostReady(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.host.ready",
            HiddenReason = "宿主装载生命周期回调，不由人或远端触发",
            Domain = "aurora",
            CommandClass = "host",
            Summary = "宿主通知模块装载已完成；界面据此整轮重拉页面、动作与窗格",
            Readonly = false,
            RequiresUiThread = true,
            Handler = async _ =>
            {
                // 撤掉排着队的那一轮防抖：宿主刚说完「齐了」，紧接着再问一遍
                // 同一批模块只是白跑，而冷启动那一刻正是最不该浪费的时候。
                s.Window.CancelScheduledDiscover();

                var report = await s.Window.DiscoverModuleSurfacesAsync().ConfigureAwait(true);
                return report == null
                    ? CommandResult.Ok("宿主已就绪；本轮界面发现失败，原因见 ui-claim 日志")
                    : CommandResult.Ok("宿主已就绪，" + DescribeReady(report), report);
            },
        });
    }

    private static string DescribeReady(PageLoadReport report)
    {
        var text = $"已重拉: 问了 {report.ModulesAsked} 个模块，建了 {report.PagesRegistered} 页";
        if (report.Skipped.Count > 0)
            text += "；跳过 " + string.Join("、", report.Skipped);
        if (report.Missing.Count > 0)
            text += $"；{report.Missing.Count} 处缺件（aurora.ui.missing 可查）";
        return text;
    }
}
