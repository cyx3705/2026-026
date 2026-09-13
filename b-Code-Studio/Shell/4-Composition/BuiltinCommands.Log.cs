using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell.HostedPages.Console;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Components.Panels;

namespace HistoryAurora.Shell.Composition;

internal static partial class BuiltinCommands
{
    private static void RegisterLog(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.snapshot",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "读取当前前端控制台内存日志快照",
            Example = "aurora.log.snapshot minlevel=error source=shell.chrome limit=100",
            HiddenReason = "Diana 进程内只读提供者，不直接对远程暴露",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec { Name = "minlevel", Description = "trace/debug/info/warn/error/fatal", Default = "error", AllowedValues = ["trace", "debug", "info", "warn", "error", "fatal"] },
                new ParameterSpec { Name = "source", Description = "日志来源精确匹配" },
                new ParameterSpec { Name = "keyword", Description = "来源或完整消息包含匹配" },
                new ParameterSpec { Name = "since", Description = "ISO 8601 起始时间" },
                new ParameterSpec { Name = "after", Description = "只返回该单调序号之后的记录" },
                new ParameterSpec { Name = "limit", Description = "返回条数，范围 1~500", Type = ParamType.Int, Default = "100" },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (s.Log is not MemoryShellLog log)
                    return CommandResult.Fail("当前 Aurora 控制台快照提供者不可用");

                var minimumLevelText = ctx.GetString("minlevel") ?? "error";
                if (!Enum.TryParse<ShellLogLevel>(minimumLevelText, true, out var minimumLevel)
                    || !Enum.IsDefined(minimumLevel))
                {
                    return CommandResult.Fail($"未知日志级别: {minimumLevelText}");
                }

                var limit = ctx.GetInt("limit", 100);
                if (limit is < 1 or > 500)
                    return CommandResult.Fail("limit 必须在 1~500 之间");

                DateTimeOffset? since = null;
                if (ctx.GetString("since") is { Length: > 0 } sinceText)
                {
                    if (!DateTimeOffset.TryParse(
                            sinceText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                            out var parsedSince))
                    {
                        return CommandResult.Fail("since 必须是有效的 ISO 8601 时间");
                    }
                    since = parsedSince;
                }

                long? after = null;
                if (ctx.GetString("after") is { Length: > 0 } afterText)
                {
                    if (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAfter)
                        || parsedAfter < 0)
                    {
                        return CommandResult.Fail("after 必须是非负整数");
                    }
                    after = parsedAfter;
                }

                var snapshot = log.ReadSnapshot(new ConsoleLogQuery(
                    minimumLevel,
                    Normalize(ctx.GetString("source")),
                    Normalize(ctx.GetString("keyword")),
                    since,
                    after,
                    limit));
                return CommandResult.Ok(
                    $"控制台日志匹配 {snapshot.MatchedCount} 条，返回 {snapshot.ReturnedCount} 条",
                    JsonSerializer.SerializeToElement(snapshot));
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.level",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "设置控制台显示级别",
            Example = "aurora.log.level level=warn",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "level", Description = "trace/debug/info/warn/error/fatal；省略时查询当前值", Position = 0, AllowedValues = ["trace", "debug", "info", "warn", "error", "fatal"] }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = ctx.GetString("level");
                if (text == null)
                    return CommandResult.Ok($"当前控制台级别: {s.Console.MinLevel.ToString().ToLowerInvariant()}");
                if (!Enum.TryParse<ShellLogLevel>(text, true, out var level))
                    return CommandResult.Fail($"未知日志级别: {text}");
                s.Console.SetMinLevel(level);
                return CommandResult.Ok($"控制台级别已设置为 {level.ToString().ToLowerInvariant()}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.app.window",
            Domain = "aurora",
            CommandClass = "app",
            Summary = "设置主窗口状态",
            Example = "aurora.app.window state=toggle",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "state",
                    Description = "normal/minimized/maximized/toggle；省略时查询当前值",
                    Position = 0,
                    AllowedValues = ["normal", "minimized", "maximized", "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var state = ctx.GetString("state");
                if (state == null)
                    return CommandResult.Ok($"主窗口状态: {s.Window.WindowState.ToString().ToLowerInvariant()}");

                s.Window.WindowState = state.ToLowerInvariant() switch
                {
                    "normal" => WindowState.Normal,
                    "minimized" => WindowState.Minimized,
                    "maximized" => WindowState.Maximized,
                    _ => s.Window.WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized,
                };
                return CommandResult.Ok($"主窗口状态已设置为 {s.Window.WindowState.ToString().ToLowerInvariant()}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.source",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "设置控制台日志域过滤（兼容命令名）",
            Example = "aurora.log.source source=app",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "source", Description = "命令总线当前已注册的域；省略时查询当前值", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var source = ctx.GetString("source");
                if (source == null)
                    return CommandResult.Ok($"当前日志域: {s.Console.SourceFilterValue}");
                if (!s.Console.TrySetSource(source, out var available))
                    return CommandResult.Fail($"日志域不存在: {source}；可用域: {string.Join(" / ", available)}");
                return CommandResult.Ok($"日志域已设置为 {s.Console.SourceFilterValue}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.class",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "设置控制台和命令集的命令类过滤",
            Example = "aurora.log.class class=win",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "class", Description = "当前域内的命令类；省略时查询当前值", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var commandClass = ctx.GetString("class");
                if (commandClass == null)
                    return CommandResult.Ok($"当前命令类: {s.Console.ClassFilterValue}");
                if (!s.Console.TrySetClass(commandClass, out var available))
                    return CommandResult.Fail($"命令类不存在: {commandClass}；可用类: {string.Join(" / ", available)}");
                return CommandResult.Ok($"命令类已设置为 {s.Console.ClassFilterValue}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.keyword",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "设置控制台关键字过滤",
            Example = "aurora.log.keyword text=timeout",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "text", Description = "关键字；省略时查询当前值", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = ctx.GetString("text");
                if (text == null)
                    return CommandResult.Ok(string.IsNullOrEmpty(s.Console.KeywordFilterValue) ? "当前关键字: (无)" : $"当前关键字: {s.Console.KeywordFilterValue}");
                s.Console.SetKeyword(text);
                return CommandResult.Ok(string.IsNullOrEmpty(text) ? "关键字过滤已清除" : $"关键字已设置为 {text}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.mute",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "屏蔽或恢复 layout 来源",
            Example = "aurora.log.mute layout=true",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "layout", Description = "true/false；省略时查询当前值", Type = ParamType.Bool, Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.Has("layout"))
                    return CommandResult.Ok($"layout 屏蔽: {s.Console.MuteLayoutEnabled}");
                var enabled = ctx.GetBool("layout");
                s.Console.SetMuteLayout(enabled);
                return CommandResult.Ok($"layout 屏蔽已设置为 {enabled}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.autoscroll",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "设置控制台自动滚动",
            Example = "aurora.log.autoscroll enabled=false",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "enabled", Description = "true/false；省略时查询当前值", Type = ParamType.Bool, Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (!ctx.Has("enabled"))
                    return CommandResult.Ok($"自动滚动: {s.Console.AutoScrollEnabled}");
                var enabled = ctx.GetBool("enabled");
                s.Console.SetAutoScroll(enabled);
                return CommandResult.Ok($"自动滚动已设置为 {enabled}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.clear",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "清空控制台可见缓冲",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Console.Cls();
                return CommandResult.Ok("控制台已清空");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.export",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "导出控制台当前可见内容",
            Example = "aurora.log.export path=console.txt",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "path", Description = "目标文件路径；省略时写入默认导出目录", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx => CommandResult.Ok(s.Console.ExportVisible(ctx.GetString("path")))),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.copy",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "复制控制台选中行",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(s.Console.CopySelected())),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.prefill",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "把一条指令填进控制台输入框（不执行）",
            Example = "aurora.log.prefill text=vulcan.module.list",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "text",
                    Description = "要填入的文本",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var text = ctx.RequireString("text");
                s.Docking.Show(StandardWindowIds.Console);
                s.Console.Prefill(text);
                return CommandResult.Ok("已填入控制台，未执行");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.log.focus",
            Domain = "aurora",
            CommandClass = "log",
            Summary = "聚焦控制台，可选仅显示错误",
            Example = "aurora.log.focus errors=true",
            RequiresUiThread = true,
            Parameters = [new ParameterSpec { Name = "errors", Description = "true 时切换到错误过滤", Type = ParamType.Bool, Default = "false", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var errors = ctx.GetBool("errors");
                if (errors)
                    s.Console.FilterErrorsOnly();
                s.Docking.Show(StandardWindowIds.Console);
                s.Console.FocusInput();
                return CommandResult.Ok(errors ? "已聚焦控制台错误" : "已聚焦控制台");
            }),
        });
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
