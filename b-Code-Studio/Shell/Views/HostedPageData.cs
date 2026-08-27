using System.Globalization;
using System.Text.Json;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.CommandSurface;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Views;

/// <summary>
/// 自持页面的取数与动作（REQ-UI-053）。
///
/// **这是「自己消费自己」的代价，也是它的全部收益。** 页面改成描述式之后，
/// 三页原本写在 C# 里的取数逻辑必须变成一条模块也能调的只读命令——
/// 于是「命令集里有哪些指令」「装了哪些模块」这些事实，从界面私有的内存状态
/// 变成了总线上人人可查的东西。1.8.18 之前想在控制台看一眼模块清单，
/// 只能去调宿主的 <c>vulcan.module.list</c> 再自己对着 JSON 数；现在
/// <c>aurora.ui.data view=modules</c> 给的就是页面上看到的那张表。
///
/// 一条命令带 <c>view</c> 参数、而不是四条命令：<c>&lt;域&gt;.ui.data</c> 是协议
/// 约定的形状（页面注册协议 §1.4），模块那一侧也是这么一条。界面自己没有理由用另一种。
/// </summary>
internal static class HostedPageData
{
    /// <summary>目录会话与总线在装配根之后才存在，因此这里收访问器而不是实例。</summary>
    internal sealed class Sources
    {
        public Func<CommandBus?>? Bus { get; init; }

        public Func<LocalCommandCatalogSession?>? Catalog { get; init; }
    }

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// 登记取数与自持指令。幂等：已在注册表里的不再登记第二遍。
    ///
    /// **必须与内置指令组同期登记**，不能等页面打开。界面总线默认把命令发给宿主
    /// （<c>AuroraShellHost.WireBuses</c>），而宿主注册表只收 Attach 那一刻抄过去的那批；
    /// 晚于 Attach 登记的界面命令在宿主那边永远不存在，症状是一条
    /// <c>✗ 未知指令: aurora.ui.data</c>，而本机注册表里它明明在（1.8.10 实测）。
    /// </summary>
    public static void Register(CommandRegistry registry, Sources sources)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sources);

        Add(registry, new CommandDescriptor
        {
            Name = "aurora.ui.data",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "自持页面的取数：命令目录、指令详情与模块清单",
            Example = "aurora.ui.data view=commands query=git",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "view",
                    Description = "commands / commanddetail / commandparams / modules",
                    Required = true,
                    Position = 0,
                    AllowedValues = ["commands", "commanddetail", "commandparams", "modules"],
                },
                new ParameterSpec { Name = "query", Description = "commands 视图的搜索词", Position = 1 },
                new ParameterSpec { Name = "name", Description = "详情与参数视图的指令名", Position = 2 },
            ],
            Handler = async context =>
            {
                var view = (context.GetString("view") ?? "").Trim().ToLowerInvariant();
                return view switch
                {
                    "commands" => await CommandsAsync(sources, context.GetString("query")).ConfigureAwait(false),
                    "commanddetail" => await DetailAsync(sources, context.GetString("name")).ConfigureAwait(false),
                    "commandparams" => await ParametersAsync(sources, context.GetString("name")).ConfigureAwait(false),
                    "modules" => await ModulesAsync(sources).ConfigureAwait(false),
                    _ => CommandResult.Fail($"未知的 view: {view}"),
                };
            },
        });

        // 「运行」这条行操作的安全护栏。**它必须是一条指令，不能留在视图里**——
        // 页面改成描述式之后，行操作只能落到一条指令上，而护栏本身不能跟着视图一起删。
        //
        // 在指令目录里一键执行一条会改东西的指令，而且往往是在「我只是想看看它干什么」
        // 的时候，是这一页最容易出事故的地方。只读的直接跑，其余一律只填进输入框。
        Add(registry, new CommandDescriptor
        {
            Name = "aurora.command.runreadonly",
            Domain = "aurora",
            CommandClass = "command",
            Summary = "只读指令直接执行；非只读指令只填进控制台，不执行",
            Example = "aurora.command.runreadonly name=vulcan.module.list",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "指令名", Required = true, Position = 0 },
            ],
            Handler = async context =>
            {
                var name = (context.GetString("name") ?? "").Trim();
                if (name.Length == 0)
                    return CommandResult.Fail("缺少 name");
                if (sources.Bus?.Invoke() is not { } bus)
                    return CommandResult.Fail("命令总线尚未就绪");

                // 判据取本机注册表。取不到描述符时按**非只读**处理：
                // 「不知道它是否安全」和「知道它不安全」在这里必须是同一条路。
                var readOnly = bus.Registry.TryGet(name, out var descriptor) && descriptor.Readonly;
                if (readOnly)
                    return await bus.ExecuteAsync(name, "UI").ConfigureAwait(true);

                await bus
                    .ExecuteAsync("aurora.log.prefill text=" + CommandParser.QuoteArg(name), "UI")
                    .ConfigureAwait(true);
                return CommandResult.Ok(name + " 不是只读指令，已填入控制台待确认，未执行");
            },
        });

        // 「刷新模块」与「热重载」都是**两步**：先动模块，再让表格重取。
        //
        // 组合下沉成命令，而不是让页面描述学会「先做这个再做那个」。描述一旦有了顺序，
        // 它就从「这一页长什么样」变成了一门脚本语言，而这套协议的全部价值恰恰在于
        // 它**不是**脚本语言——模块交数据，界面画界面（DEC-005）。
        Add(registry, new CommandDescriptor
        {
            Name = "aurora.module.reloadall",
            HiddenReason = "界面自持页面的组合指令",
            Domain = "aurora",
            CommandClass = "module",
            Summary = "重载模块并刷新模块管理页",
            RequiresUiThread = true,
            Handler = async context =>
            {
                if (sources.Bus?.Invoke() is not { } bus)
                    return CommandResult.Fail("命令总线尚未就绪");

                var reloaded = await bus.ExecuteAsync("vulcan.module.reload", "UI").ConfigureAwait(true);
                if (!reloaded.Success)
                    return CommandResult.Fail("模块重载失败: " + FirstLine(reloaded.Message));

                await RefreshModulesAsync(bus).ConfigureAwait(true);
                return CommandResult.Ok(FirstLine(reloaded.Message));
            },
        });

        Add(registry, new CommandDescriptor
        {
            Name = "aurora.module.hotreload",
            HiddenReason = "界面自持页面的组合指令",
            Domain = "aurora",
            CommandClass = "module",
            Summary = "选一个目录安装为模块，然后刷新模块管理页",
            RequiresUiThread = true,
            Handler = async context =>
            {
                if (sources.Bus?.Invoke() is not { } bus)
                    return CommandResult.Fail("命令总线尚未就绪");

                var picked = await bus.ExecuteAsync("aurora.ui.selectdirectory", "UI").ConfigureAwait(true);
                if (!picked.Success)
                    return CommandResult.Fail(FirstLine(picked.Message));
                if (!CommandResultData.TryRead<string>(picked.Data, out var path)
                    || string.IsNullOrWhiteSpace(path))
                    return CommandResult.Ok("已取消热重载");

                var installed = await bus
                    .ExecuteAsync($"vulcan.module.install path={CommandParser.QuoteArg(path)}", "UI")
                    .ConfigureAwait(true);
                if (!installed.Success)
                {
                    // 这条提示是 1.7 真机上攒出来的：装不上时人第一反应是「再选一次」，
                    // 而正确的下一步是换一个候选目录，且**不会自动恢复**。
                    return CommandResult.Fail(
                        "热重载失败: " + FirstLine(installed.Message)
                        + "。请再选主树候选或 z-Publish/history/HistoryX-vX.Y.Z，不会自动恢复。");
                }

                await RefreshModulesAsync(bus).ConfigureAwait(true);
                return CommandResult.Ok(FirstLine(installed.Message));
            },
        });
    }

    /// <summary>
    /// 自持页面的动作声明。与模块的 <c>&lt;域&gt;.ui.actions</c> 同一个结构，
    /// 只是由界面自己经 <see cref="ActionRegistry.DeclareLocal"/> 交上去——
    /// 界面自带的页面不该把自己伪装成模块（与 REQ-UI-020 同一条理由）。
    /// </summary>
    public static IReadOnlyList<ActionDeclaration> Actions =>
    [
        new ActionDeclaration
        {
            Id = "modules.reload",
            Title = "刷新模块",
            Command = "aurora.module.reloadall",
            Summary = "重新扫描运行区并刷新清单",
        },
        new ActionDeclaration
        {
            Id = "modules.hotreload",
            Title = "热重载",
            Command = "aurora.module.hotreload",
            Summary = "选一个目录安装为模块",
        },
        new ActionDeclaration
        {
            Id = "modules.opendir",
            Title = "打开发现根",
            // 没有第二步，因此**不包一层**：能直接绑宿主指令的动作就直接绑。
            // 多一层壳只会多一个改名时会漏掉的地方。
            Command = "vulcan.module.open",
            Summary = "在资源管理器里打开模块发现根",
        },
        // 命令集的四个行操作。行操作里的 {列名} 默认取**被操作那一行**的同名列，
        // 因此这里写 {name} 就够了，不必绕道选择通道（协议 §PageRowAction）。
        new ActionDeclaration
        {
            Id = "mcp.detail",
            Title = "详情",
            Command = "aurora.ui.show",
            Args = new Dictionary<string, string> { ["name"] = "commanddetail" },
            Summary = "在指令详情页展开这条指令",
        },
        new ActionDeclaration
        {
            Id = "mcp.prefill",
            Title = "填入",
            Command = "aurora.log.prefill",
            Args = new Dictionary<string, string> { ["text"] = "{name}" },
            Summary = "把指令名填进控制台输入框，不执行",
        },
        new ActionDeclaration
        {
            Id = "mcp.copyexample",
            Title = "复制示例",
            Command = "aurora.command.copyexample",
            Args = new Dictionary<string, string> { ["name"] = "{name}" },
            // 1.8.18 之前这一条复制的是**指令名**，靠视图里一句 Clipboard.SetText。
            // 现成的指令只有「复制示例」，而为了少写一行 C# 去新增一条
            // 「复制指令名」指令并不划算——指令名就在眼前，示例才是需要复制的那个。
            Summary = "把这条指令的示例复制到剪贴板",
        },
        new ActionDeclaration
        {
            Id = "mcp.run",
            Title = "运行（仅只读指令）",
            Command = "aurora.command.runreadonly",
            Args = new Dictionary<string, string> { ["name"] = "{name}" },
            Summary = "只读指令直接执行，其余只填进控制台",
        },
    ];

    private static async Task RefreshModulesAsync(CommandBus bus)
    {
        // 刷不到也不算失败：模块管理页可能压根没开着，那时「没有匹配的取数节点」
        // 是正常的，不该把一次成功的重载报成失败。
        try
        {
            await bus.ExecuteAsync("aurora.ui.refreshdata page=modules", "UI").ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 同上：刷新是附带动作，不能反过来决定主动作的成败。
        }
    }

    private static async Task<CommandResult> CommandsAsync(Sources sources, string? query)
    {
        if (sources.Catalog?.Invoke() is not { } catalog)
            return Rows([]);

        await catalog.RefreshAsync().ConfigureAwait(true);

        var needle = (query ?? "").Trim();
        var rows = catalog.Entries
            .Where(entry => Matches(entry, needle))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new Dictionary<string, string>
            {
                ["name"] = entry.Name,
                ["domain"] = entry.Domain,
                ["class"] = CommandClassLabels.Display(entry.CommandClass),
                ["readonly"] = entry.Readonly ? "是" : "",
                ["summary"] = entry.Summary,
            })
            .ToList();

        return Rows(rows);
    }

    /// <summary>
    /// 搜索横跨指令名、说明与域。
    ///
    /// 1.8.18 之前这一页有「域」「类」两个联动下拉：域选了才能选类，两级严格联动
    /// （DEC-021）。它们随描述式改造一起去掉了——联动下拉的候选是动态的，
    /// 而控制面板的选项框只收静态候选。**与其给面板加一种动态候选，不如让搜索词
    /// 也能匹配域**：想只看 aurora 的，输 `aurora.` 就是了。
    ///
    /// 这是本轮「页面适配组件、组件保持克制」的一处具体取舍，能力确实少了一点，
    /// 少的那一点写在这里，不假装没发生。
    /// </summary>
    private static bool Matches(CatalogEntry entry, string needle)
    {
        if (needle.Length == 0)
            return true;

        return entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Domain.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CommandResult> DetailAsync(Sources sources, string? name)
    {
        if (sources.Catalog?.Invoke() is not { } catalog || string.IsNullOrWhiteSpace(name))
            return Rows([]);

        var detail = await catalog.DetailAsync(name).ConfigureAwait(true);
        if (detail == null)
            return Rows([Pair("指令", name), Pair("状态", "查不到这条指令")]);

        var row = detail.Command;
        var flags = new List<string> { CommandClassLabels.Display(row.CommandClass) };
        if (row.Readonly)
            flags.Add("只读");
        if (row.Dangerous)
            flags.Add("执行前询问");
        if (row.RequiresUiThread)
            flags.Add("界面线程");
        if (row.HiddenReason != null)
            flags.Add("远端隐藏：" + row.HiddenReason);

        var rows = new List<Dictionary<string, string>>
        {
            Pair("指令", row.CommandName),
            Pair("说明", row.Summary),
            Pair("域", row.Domain),
            Pair("标志", string.Join(" · ", flags)),
            Pair("来源", row.Source),
        };

        if (!string.IsNullOrWhiteSpace(row.Example))
            rows.Add(Pair("示例", row.Example));

        return Rows(rows);
    }

    private static async Task<CommandResult> ParametersAsync(Sources sources, string? name)
    {
        if (sources.Catalog?.Invoke() is not { } catalog || string.IsNullOrWhiteSpace(name))
            return Rows([]);

        var detail = await catalog.DetailAsync(name).ConfigureAwait(true);
        if (detail == null)
            return Rows([]);

        var rows = detail.Parameters
            .Select(parameter => new Dictionary<string, string>
            {
                ["name"] = parameter.Name,
                ["type"] = parameter.Type,
                ["required"] = parameter.Required ? "是" : "",
                ["summary"] = Describe(parameter),
            })
            .ToList();

        return Rows(rows);
    }

    /// <summary>允许值并进说明列：它是「这个参数能填什么」的一部分，不该另占一列。</summary>
    private static string Describe(HistoryVulcan.Services.Commands.CommandParameterInfo parameter)
        => parameter.AllowedValues.Count == 0
            ? parameter.Description
            : parameter.Description + "（可选值：" + string.Join(" / ", parameter.AllowedValues) + "）";

    private static async Task<CommandResult> ModulesAsync(Sources sources)
    {
        if (sources.Bus?.Invoke() is not { } bus)
            return Rows([]);

        var result = await ModuleCatalogReader.LoadModulesAsync(bus).ConfigureAwait(true);
        if (!result.Success || result.Snapshot is not { } snapshot)
            return CommandResult.Fail(result.Message);

        var rows = snapshot.Modules
            .Select(module => new Dictionary<string, string>
            {
                ["module"] = module.ModuleName,
                ["version"] = module.Version,
                ["commands"] = DomainCommandCount(bus.Registry, module.ModuleName)
                    .ToString(CultureInfo.InvariantCulture),
                ["description"] = module.Description,
            })
            .ToList();

        return Rows(rows);
    }

    /// <summary>
    /// 按域统计当前注册表里的指令条数；域名取自模块名（History 前缀剥离）。
    ///
    /// 这是**该域当前注册的指令总数**，不是本模块经模块路径注册的条数。二者对四个业务
    /// 模块相等，对 HistoryAurora 却差得很远：Aurora 是应用，它的 aurora.* 由应用进程
    /// 自持并上报，经模块路径注册的是 0 条（DEC-007）。显示 0 会让人以为它坏了。
    /// </summary>
    private static int DomainCommandCount(CommandRegistry registry, string moduleName)
    {
        var domain = ModuleDomainNaming.ToDomain(moduleName);
        if (domain.Length == 0)
            return 0;

        return registry.All().Count(descriptor =>
            string.Equals(DomainOf(descriptor), domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>描述符未声明域时按指令名首段兜底，与注册表的归一化口径一致。</summary>
    private static string DomainOf(CommandDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.Domain))
            return descriptor.Domain;
        var separator = descriptor.Name.IndexOf('.');
        return separator > 0 ? descriptor.Name[..separator] : descriptor.Name;
    }

    private static Dictionary<string, string> Pair(string key, string value)
        => new() { ["key"] = key, ["value"] = value };

    /// <summary>
    /// 取数结果同时放进 <c>Data</c> 与 <c>Message</c>。
    ///
    /// <c>CommandResult.Data</c> 是 <c>object?</c>，跨进程中继后结构化载荷不会原样存活；
    /// 渲染器因此优先读 Data、回退 Message（协议 §四）。自持页面走不到中继，
    /// 但**取数命令的形状必须与模块那一侧一致**——否则模块作者照着它写就会踩坑。
    /// </summary>
    private static CommandResult Rows(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var json = JsonSerializer.Serialize(rows, Compact);
        return CommandResult.Ok(json, json);
    }

    private static void Add(CommandRegistry registry, CommandDescriptor descriptor)
    {
        if (registry.TryGet(descriptor.Name, out _))
            return;
        registry.Register(descriptor, FrontendCommandCatalog.Source);
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;
}
