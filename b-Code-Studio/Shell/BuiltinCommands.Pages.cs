using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Pages;

namespace HistoryAurora.Shell;

/// <summary>
/// 页面注册协议 V1 的 Aurora 侧命令（协议 §1.4）。
///
/// 模块只连宿主总线、不认识 Aurora：它调 <c>aurora.ui.invalidate</c> 与调自己的命令没有区别，
/// 宿主只负责路由，不解释载荷。因此模块无需引用 Aurora 的任何程序集。
/// </summary>
public static partial class BuiltinCommands
{
    private static void RegisterPages(CommandRegistry r, ModulePageLoader loader)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.reloadpages",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "重新向全部模块拉取页面描述并建页",
            RequiresUiThread = true,
            Handler = async _ =>
            {
                var report = await loader.ReloadAsync().ConfigureAwait(true);
                return CommandResult.Ok(Describe(report));
            },
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.invalidate",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "模块声明自己的页面描述已变，请求重拉该模块",
            Example = "aurora.ui.invalidate owner=HistoryMercury",
            RequiresUiThread = true,
            AllowUnspecifiedParameters = true,
            Handler = async context =>
            {
                var owner = context.GetString("owner");
                if (string.IsNullOrWhiteSpace(owner))
                    return CommandResult.Fail("缺少 owner");
                var report = await loader.ReloadOwnerAsync(owner).ConfigureAwait(true);
                return CommandResult.Ok(Describe(report));
            },
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.missing",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出页面描述里引用了、但组件库尚未提供的组件",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var missing = loader.Missing;
                if (missing.Count == 0)
                    return CommandResult.Ok("无缺件");

                var text = new StringBuilder();
                foreach (var group in missing.GroupBy(m => m.Component, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var users = group
                        .Select(m => m.Owner + "/" + m.PageId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
                    text.AppendLine($"{group.Key}  ← {string.Join("、", users)}");
                }

                return CommandResult.Ok(text.ToString().TrimEnd());
            }),
        });
    }

    private static string Describe(PageLoadReport report)
    {
        var text = $"问了 {report.ModulesAsked} 个模块，建了 {report.PagesRegistered} 页";
        if (report.Skipped.Count > 0)
            text += "；跳过 " + string.Join("、", report.Skipped);
        if (report.Missing.Count > 0)
            text += $"；{report.Missing.Count} 处缺件（aurora.ui.missing 可查）";
        return text;
    }
}
