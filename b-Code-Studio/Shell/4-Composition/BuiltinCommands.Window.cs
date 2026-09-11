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
    private static void RegisterWin(CommandRegistry r, ShellCommandServices s)
    {
        var nameParam = new ParameterSpec
        {
            Name = "name",
            Description = "窗口名(aurora.ui.windows 可查)",
            Required = true,
            Position = 0,
        };

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.windows",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出全部窗口及状态",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var sb = new StringBuilder("窗口清单:");
                foreach (var w in s.Docking.ListWindows())
                {
                    var state = !w.IsVisible ? "隐藏"
                        : w.IsFloating ? "浮动"
                        : w.Side switch
                        {
                            DockSide.Left => "停靠·左",
                            DockSide.Right => "停靠·右",
                            DockSide.Top => "停靠·上",
                            DockSide.Bottom => "停靠·下",
                            DockSide.Center => "中央区",
                            _ => "停靠",
                        };
                    var ratio = w.Ratio is { } v and > 0 ? $" {v:P0}" : "";
                    var maximized = s.Docking.MaximizedId?.Equals(
                        w.Id, StringComparison.OrdinalIgnoreCase) == true ? " [最大化]" : "";
                    sb.Append($"\n  {w.Id,-12} {state}{ratio}{maximized}  {w.Title}  owner={w.Owner}");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        RegisterWindowVerb(r, s, "aurora.ui.show", "显示窗口(隐藏则唤出,已显示则激活)",
            (d, id) => { d.Show(id); return $"{id} 已显示"; });
        RegisterWindowVerb(r, s, "aurora.ui.hide", "隐藏窗口(状态保留,可再唤出)",
            (d, id) => { d.Hide(id); return $"{id} 已隐藏"; });
        RegisterWindowVerb(r, s, "aurora.ui.float", "把窗口浮动为独立顶层窗口",
            (d, id) => { d.Float(id); return $"{id} 已浮动"; });
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.autohide",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "切换工具窗口的自动隐藏状态",
            Example = $"aurora.ui.autohide name={StandardWindowIds.Console}",
            RequiresUiThread = true,
            Parameters = [nameParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                if (s.Docking is not DockingHost host)
                    return CommandResult.Fail("当前停靠宿主不支持自动隐藏");

                var id = ctx.RequireString("name");
                try
                {
                    host.ToggleAutoHide(id);
                    return CommandResult.Ok($"{id} 已切换自动隐藏状态");
                }
                catch (InvalidOperationException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });
        RegisterWindowVerb(r, s, "aurora.ui.reset", "把窗口复位到注册时的默认位置",
            (d, id) => { d.ResetWindow(id); return $"{id} 已复位到默认位置"; });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.max",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "最大化指定工具窗口",
            Example = "aurora.ui.max name=se2sw",
            RequiresUiThread = true,
            Parameters = [nameParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("name");
                var exists = s.Docking.ListWindows().Any(w =>
                    w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    return CommandResult.Fail($"没有名为 {id} 的窗口");
                return Docking(s, "aurora.ui.max", () =>
                {
                    s.Docking.MaximizeWindow(id);
                    return $"{id} 已最大化";
                });
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.floatstate",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "设置独立浮窗宿主的最大化状态",
            Example = $"aurora.ui.floatstate name={StandardWindowIds.Console} state=toggle",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "state",
                    Description = "maximized、normal 或 toggle",
                    Default = "toggle",
                    Position = 1,
                    AllowedValues = ["maximized", "normal", "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;
                return s.Window.SetFloatingWindowState(
                    ctx.RequireString("name"), ctx.GetString("state") ?? "toggle");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.restore",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "退出窗口最大化并恢复原布局",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => Docking(s, "aurora.ui.restore", () =>
            {
                s.Docking.RestoreLayoutFromMaximized();
                return "已恢复原布局";
            })),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.dock",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "停靠窗口到指定方位，那个位置原来的页被隐藏(一格一页；pos=center 占中央区)",
            Example = $"aurora.ui.dock name={StandardWindowIds.Console} pos=bottom ratio=0.3",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "pos",
                    Description = "停靠方位",
                    Required = true,
                    AllowedValues = ["left", "right", "top", "bottom", "center"],
                },
                new ParameterSpec
                {
                    Name = "ratio",
                    Description = "四边停靠比例；提供时须严格位于 (0,1)，Center 不使用",
                    Type = ParamType.Double,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                var id = ctx.RequireString("name");
                var side = ParseSide(ctx.RequireString("pos"));
                var ratio = ctx.Has("ratio") ? ctx.GetDouble("ratio") : (double?)null;
                if (ratio is { } ratioValue &&
                    (!double.IsFinite(ratioValue) || ratioValue is <= 0 or >= 1))
                    return CommandResult.Fail($"ratio 应严格位于 (0,1),实际: {ratio}");

                s.Docking.Dock(id, side, ratio);
                var where = side switch
                {
                    DockSide.Left => "左侧",
                    DockSide.Right => "右侧",
                    DockSide.Top => "顶部",
                    DockSide.Bottom => "底部",
                    _ => "中央区",
                };
                var pct = ratio is { } rv ? $"({rv:P0})" : "";
                return CommandResult.Ok($"{id} 已停靠至{where}{pct}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.ratio",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "调整窗口占主窗体的比例",
            Example = $"aurora.ui.ratio name={StandardWindowIds.Console} value=0.3",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "value",
                    Description = "四边停靠比例，须严格位于 (0,1)",
                    Required = true,
                    Type = ParamType.Double,
                    Position = 1,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                var value = ctx.GetDouble("value");
                if (!double.IsFinite(value) || value is <= 0 or >= 1)
                    return CommandResult.Fail($"value 应严格位于 (0,1),实际: {value}");

                var id = ctx.RequireString("name");
                s.Docking.SetRatio(id, value);
                return CommandResult.Ok($"{id} 比例已调整为 {value:P0}");
            }),
        });
    }

    /// <summary>
    /// 跑一次停靠动作，并**把真实异常先记进本端日志**。
    ///
    /// 指令总线对外只回「执行异常(类型名)」——它刻意不把 Message 与堆栈发出去
    /// （`CommandBus` 里 `safeError = ex.GetType().Name`）。这条策略对远端是对的，
    /// 但本机排查也只剩一个类型名：2026-08-25 真机报
    /// `aurora.ui.max 执行异常(NotSupportedException)`，日志里没有任何能定位到行的东西，
    /// 而这一条在自动化里复现不出来（浮窗、模块页、逐个窗口最大化都试过，全绿）。
    /// 所以停靠动作在这里先把完整异常写进 Aurora 自己的控制台，再让它照常抛出去。
    /// </summary>
    private static CommandResult Docking(ShellCommandServices s, string command, Func<string> action)
    {
        try
        {
            return CommandResult.Ok(action());
        }
        catch (Exception ex)
        {
            s.Log.Error("dock", $"{command} 失败: {ex}");
            throw;
        }
    }

    private static void RegisterWindowVerb(
        CommandRegistry r,
        ShellCommandServices s,
        string name,
        string summary,
        Func<IDockingService, string, string> action)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = name,
            // 这里的 Name 是参数而非字面量，因此改域时按 `Name = "aurora.*"` 做的批量同步
            // 漏掉了本处——症状是命令名已是 aurora.* 而域仍报 vulcan，在
            // `vulcan.command.list domain=vulcan` 里能查到一条名叫 aurora.ui.show 的命令。
            Domain = "aurora",
            CommandClass = "ui",
            Summary = summary,
            Example = $"{name} name={StandardWindowIds.Console}",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "窗口名(aurora.ui.windows 可查)",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;
                var id = ctx.RequireString("name");
                var result = Docking(s, name, () => action(s.Docking, id));
                if (name.Equals("aurora.ui.show", StringComparison.OrdinalIgnoreCase))
                    s.Window.ActivateToolContent(id);
                return result;
            }),
        });
    }

    /// <summary>窗口名存在性校验;返回 null 表示通过。</summary>
    private static CommandResult? ResolveWindow(ShellCommandServices s, CommandContext ctx)
    {
        var id = ctx.RequireString("name");
        if (s.Docking.ListWindows().Any(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return null;

        var known = string.Join(" / ", s.Docking.ListWindows().Select(w => w.Id));
        return CommandResult.Fail($"没有名为 {id} 的窗口。已注册: {known}");
    }

    private static DockSide ParseSide(string pos) => pos.ToLowerInvariant() switch
    {
        "left" => DockSide.Left,
        "right" => DockSide.Right,
        "top" => DockSide.Top,
        "bottom" => DockSide.Bottom,
        _ => DockSide.Center,
    };

    // ---------------------------------------------------------------- layout.*

}

