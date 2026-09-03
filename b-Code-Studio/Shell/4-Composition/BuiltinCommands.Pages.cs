using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Components.Pages;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 页面注册协议 V1 的 Aurora 侧命令（协议 §1.4）。
///
/// 模块只连宿主总线、不认识 Aurora：它调 <c>aurora.ui.invalidate</c> 与调自己的命令没有区别，
/// 宿主只负责路由，不解释载荷。因此模块无需引用 Aurora 的任何程序集。
/// </summary>
internal static partial class BuiltinCommands
{
    private static void RegisterPages(CommandRegistry r, ModulePageLoader loader, ComponentRequestStore requests)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.reloadpages",
            HiddenReason = "界面内部协议不对远程暴露",
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
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "模块声明自己的页面描述已变，请求重拉该模块",
            Example = "aurora.ui.invalidate owner=HistoryMercury",
            // 模块名与域名都收：拉描述用的是域（mercury.ui.describe），撤旧页用的是
            // 模块名（HistoryMercury），两者按设计不相等。1.7.0 起在拉取器里各归一一次。
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "owner",
                    Description = "模块名（HistoryMercury）或指令域（mercury），两种都接受",
                    Required = true,
                    Position = 0,
                },
            ],
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
            HiddenReason = "界面内部协议不对远程暴露",
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

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.request",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "申请一个组件库尚未提供的组件",
            Example = "aurora.ui.request component=segment.toggle by=HistoryJanus reason=\"分段家族缺 Toggle 成员\"",
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(context =>
            {
                var component = context.GetString("component")?.Trim();
                if (string.IsNullOrWhiteSpace(component))
                    return CommandResult.Fail("缺少 component");
                if (PageRenderer.SupportedComponents.Contains(component)
                    || PageRenderer.SupportedCapabilities.Contains(component))
                    return CommandResult.Ok($"{component} 已经支持，无需申请");

                // 模块经宿主中继调用时 context.Source 是 "Service:Relay"，会把提出方记丢，
                // 因此允许显式声明 by；缺省才回退到来源标签。
                var by = context.GetString("by")?.Trim();
                if (string.IsNullOrWhiteSpace(by))
                    by = context.Source;

                requests.Record(component, by, context.GetString("page"), context.GetString("reason"));
                return CommandResult.Ok($"已登记组件申请: {component}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.requests",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出尚未交付的组件申请（已交付的自动出账）",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var open = requests.ListOpen();
                if (open.Count == 0)
                    return CommandResult.Ok("无未交付的组件申请");

                var text = new StringBuilder();
                foreach (var request in open)
                {
                    text.Append(request.Component).Append("  ← ").Append(request.RequestedBy);
                    if (request.Pages.Count > 0)
                        text.Append("  用于 ").Append(string.Join("、", request.Pages));
                    if (!string.IsNullOrWhiteSpace(request.Reason))
                        text.Append("  理由: ").Append(request.Reason);
                    text.AppendLine();
                }

                return CommandResult.Ok(text.ToString().TrimEnd(), open);
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
