using System.Text;
using HistoryAurora.Shell.Actions;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell;

/// <summary>
/// 动作声明协议的 Aurora 侧命令（REQ-UI-009）。
///
/// 模块只连宿主总线、不认识 Aurora：它注册 <c>&lt;域&gt;.ui.actions</c> 与注册自己的命令
/// 没有区别，宿主只负责路由，不解释载荷。因此模块无需引用 Aurora 的任何程序集。
/// </summary>
internal static partial class BuiltinCommands
{
    private static void RegisterActions(CommandRegistry r, ShellCommandServices s, ActionRegistry actions)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.actions",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出模块声明的、可被按钮绑定的动作",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var declared = actions.Actions;
                var broken = actions.Broken;
                if (declared.Count == 0 && broken.Count == 0)
                    return CommandResult.Ok("尚无模块声明动作（模块需注册 <域>.ui.actions）");

                var text = new StringBuilder($"共 {declared.Count} 条动作:");
                foreach (var action in declared)
                {
                    text.Append('\n')
                        .Append("  ")
                        .Append(action.Id)
                        .Append("  →  ")
                        .Append(action.Command)
                        .Append("   (")
                        .Append(action.Owner)
                        .Append(action.Danger ? "，危险档" : "")
                        .Append(')');
                }

                // 断链单独列出：这正是「改指令导致按钮失效」的原形，
                // 现在它在按钮被点之前就已经是一条可查询的事实。
                if (broken.Count > 0)
                {
                    text.Append("\n\n").Append(broken.Count).Append(" 条断链:");
                    foreach (var item in broken)
                        text.Append("\n  ").Append(item);
                }

                return CommandResult.Ok(text.ToString(), declared);
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.reloadactions",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "重新向全部模块拉取动作声明，并按新声明重建面板",
            RequiresUiThread = true,
            Handler = async _ =>
            {
                var report = await actions.ReloadAsync().ConfigureAwait(true);

                // 重建面板不是可选的收尾：按钮「有没有落点」在构建时就定下了，
                // 只刷新台账不重建界面，屏幕上留下的仍是上一轮的判断。
                var rebuilt = s.Panels?.RebuildAll() ?? 0;

                var text = $"问了 {report.ModulesAsked} 个模块，收下 {report.ActionsDeclared} 条动作";
                if (report.Skipped.Count > 0)
                    text += "；跳过 " + string.Join("、", report.Skipped);
                if (report.Broken.Count > 0)
                    text += $"；{report.Broken.Count} 条断链（aurora.ui.actions 可查）";
                if (rebuilt > 0)
                    text += $"；已重建 {rebuilt} 个面板";
                return CommandResult.Ok(text);
            },
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.invoke",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "按动作 id 执行一条模块声明的动作",
            Example = "aurora.ui.invoke action=janus.branch.rename to=ai/foo",
            AllowUnspecifiedParameters = true,
            Handler = async context =>
            {
                var id = context.GetString("action");
                var binding = actions.Resolve(id);
                if (!binding.Ok)
                    return CommandResult.Fail(binding.Error!);

                // 占位符从本条命令的其余参数取值：这条命令是给脚本和控制台用的
                // 面板入口，面板里那份取值来自控件，两边填的是同一组占位符。
                var text = ActionRegistry.BuildCommandText(
                    binding.Action!,
                    control => context.GetString(control),
                    out var error);
                if (text == null)
                    return CommandResult.Fail(error);

                return await s.Bus.ExecuteAsync(text, context.Source).ConfigureAwait(false);
            },
        });
    }
}
