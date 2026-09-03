using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services;
using HistoryAurora.Shell.HostedPages.Console;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Composition;

internal static partial class BuiltinCommands
{
    private static void RegisterLayout(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.layoutsave",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "把当前布局保存为命名方案",
            Example = "aurora.ui.layoutsave name=调试布局",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "方案名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                s.Docking.SaveLayout(name);
                return CommandResult.Ok($"布局方案 [{name}] 已保存");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.layoutload",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "加载命名布局方案",
            Example = "aurora.ui.layoutload name=调试布局",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "方案名(aurora.ui.layouts 可查)", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                return s.Docking.LoadLayout(name)
                    ? CommandResult.Ok($"布局方案 [{name}] 已加载")
                    : CommandResult.Fail($"布局方案 [{name}] 不存在或加载失败");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.layouts",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出全部命名布局方案",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var names = s.Docking.ListLayouts();
                return names.Count == 0
                    ? CommandResult.Ok("(暂无命名布局方案,aurora.ui.layoutsave name=xxx 可保存)")
                    : CommandResult.Ok($"共 {names.Count} 个方案:" + string.Concat(names.Select(n => $"\n  {n}")));
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.layoutreset",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "重置为默认布局",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Docking.ResetLayout();
                return CommandResult.Ok("已重置为默认布局");
            }),
        });
    }

    private static void RegisterFrontend(CommandRegistry registry, CommandDescriptor descriptor)
        => registry.Register(descriptor, FrontendCommandCatalog.Source);
    // ---------------------------------------------------------------- log.*

}

