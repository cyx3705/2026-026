using System.Globalization;
using System.Text.Json;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Neutral;

namespace HistoryAurora.Shell.HostedPages.Views;

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
            Summary = "自持页面的取数：命令目录、指令详情、筛选候选与模块清单",
            Example = "aurora.ui.data view=commands domain=aurora query=git",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "view",
                    Description = "commands / commanddetail / commandparams / domains / classes / modules",
                    Required = true,
                    Position = 0,
                    AllowedValues =
                    [
                        "commands", "commanddetail", "commandparams",
                        "domains", "classes", "modules",
                    ],
                },
                new ParameterSpec { Name = "query", Description = "commands 视图的搜索词", Position = 1 },
                new ParameterSpec { Name = "name", Description = "详情与参数视图的指令名", Position = 2 },
                new ParameterSpec
                {
                    Name = "domain",
                    Description = "commands 视图的域筛选，classes 视图的取值范围；全部 或留空表示不筛",
                    Position = 3,
                },
                new ParameterSpec
                {
                    Name = "class",
                    Description = "commands 视图的类筛选；全部 或留空表示不筛",
                    Position = 4,
                },
            ],
            Handler = async context =>
            {
                var view = (context.GetString("view") ?? "").Trim().ToLowerInvariant();
                return view switch
                {
                    "commands" => await CommandsAsync(
                        sources,
                        context.GetString("query"),
                        context.GetString("domain"),
                        context.GetString("class")).ConfigureAwait(false),
                    "commanddetail" => await DetailAsync(sources, context.GetString("name")).ConfigureAwait(false),
                    "commandparams" => await ParametersAsync(sources, context.GetString("name")).ConfigureAwait(false),
                    "domains" => await DomainsAsync(sources).ConfigureAwait(false),
                    "classes" => await ClassesAsync(sources, context.GetString("domain")).ConfigureAwait(false),
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

        // 指令详情页控制面板上的同三件事（REQ-UI-058）。
        //
        // **必须是另外三条声明，不能复用上面那三条。** 占位符的作用域不一样：
        // 行操作的 {name} 取的是**被操作那一行**，而面板按钮没有"那一行"这个上下文，
        // 只能按选择通道取（{selection.<通道>.<列>}）。
        // 复用的话，面板按钮会去找一个叫 name 的控件，找不到就整条拒绝执行——
        // 症状是"按钮点了没反应"，而那正是动作声明这套东西存在的理由。
        //
        // 命令集那一列行内按钮 1.9.2 撤掉了（右键菜单保留），这三条是它们的新落点。
        new ActionDeclaration
        {
            Id = "detail.prefill",
            Title = "填入控制台",
            Command = "aurora.log.prefill",
            Args = new Dictionary<string, string>
            {
                ["text"] = "{selection." + ShellCommandChannel + ".name}",
            },
            Summary = "把当前指令名填进控制台输入框，不执行",
        },
        new ActionDeclaration
        {
            Id = "detail.copyexample",
            Title = "复制示例",
            Command = "aurora.command.copyexample",
            Args = new Dictionary<string, string>
            {
                ["name"] = "{selection." + ShellCommandChannel + ".name}",
            },
            Summary = "把当前指令的示例复制到剪贴板",
        },
        new ActionDeclaration
        {
            Id = "detail.run",
            Title = "运行（仅只读）",
            Command = "aurora.command.runreadonly",
            Args = new Dictionary<string, string>
            {
                ["name"] = "{selection." + ShellCommandChannel + ".name}",
            },
            Summary = "只读指令直接执行，其余只填进控制台",
        },
    ];

    /// <summary>
    /// 命令集把选中行发到这个通道。与 <c>ShellWindow.CommandChannel</c> 是同一个名字——
    /// 装配根那一侧按它把选中接回 <c>IShellCommandWorkbenchHost.CommandSelection</c>。
    /// </summary>
    private const string ShellCommandChannel = "aurora.mcp.command";

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

    /// <summary>筛选下拉里「不筛选」的那一项。与目录会话里的同名常量取值一致。</summary>
    public const string All = "全部";

    /// <summary>
    /// 目录会话缺席时的统一出口（REQ-UI-057）。
    ///
    /// **这里必须失败，不能返回空表。** 1.9.0 把命令集与指令详情两页改成描述式时，
    /// 装配根漏了 <c>Catalog = _catalog</c> 这一行，而当时的写法是「拿不到会话就给空表」——
    /// 于是页面画得好好的、表头齐全、一行数据没有，日志里也一个字都没有。
    /// 「没接线」和「真的一条指令都没有」在界面上长得一模一样，那正是本仓反复在消灭的形态。
    ///
    /// 目录快照那一路（<c>FrontendCommandCatalog</c>）不受影响：它只取描述符，从不执行处理器。
    /// </summary>
    private static CommandResult NoCatalog()
        => CommandResult.Fail("命令目录会话未接线：aurora.ui.data 取不到任何指令");

    /// <summary>
    /// 命令集表格的行（REQ-UI-058）。
    ///
    /// 列改成【域 / 类 / 方法 / 只读 / 说明】：完整指令名的三段本来就是同一个事实的三份，
    /// 旧版把它们并排在一起，第一列重复了后两列的全部内容。
    ///
    /// **<c>name</c> 仍然在行里，只是不再占一列**：右键菜单的四条动作、
    /// 指令详情页的两张表、以及对外契约 <c>IShellCommandWorkbenchHost.CommandSelection</c>
    /// 都按它取值。表格只画声明过的列，但选中行与行动作拿到的是整行（REQ-UI-058）。
    /// </summary>
    private static async Task<CommandResult> CommandsAsync(
        Sources sources,
        string? query,
        string? domain,
        string? commandClass)
    {
        if (sources.Catalog?.Invoke() is not { } catalog)
            return NoCatalog();

        await catalog.RefreshAsync().ConfigureAwait(true);

        var needle = (query ?? "").Trim();
        var domainFilter = Selected(domain);
        var classFilter = Selected(commandClass);
        var rows = catalog.Entries
            .Where(entry => Matches(entry, needle, domainFilter, classFilter))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new Dictionary<string, string>
            {
                ["name"] = entry.Name,
                ["domain"] = entry.Domain,
                // 无类指令这一格留空，不写「无类」：表格里一列整齐的「无类」
                // 比空白更吵，而空白本身就是「没有这一段」。
                // 下拉候选里仍然是「无类」——那里需要一个选得中的标签。
                ["class"] = entry.CommandClass ?? "",
                ["method"] = Method(entry),
                ["readonly"] = entry.Readonly ? "是" : "",
                ["summary"] = entry.Summary,
            })
            .ToList();

        return Rows(rows);
    }

    /// <summary>
    /// 指令名去掉 <c>&lt;域&gt;.&lt;类&gt;.</c> 前缀剩下的全部。
    ///
    /// 不是「取第三段」：四段名的指令取第三段会把后面那段静默丢掉，
    /// 而两条同域同类、只差最后一段的指令会在表上变成两行完全一样的内容。
    /// 无类指令只剥 <c>&lt;域&gt;.</c>。
    /// </summary>
    internal static string Method(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var name = entry.Name ?? "";
        var prefix = (entry.Domain ?? "") + ".";
        if (prefix.Length > 1 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];

        var classPrefix = (entry.CommandClass ?? "") + ".";
        if (classPrefix.Length > 1 && name.StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[classPrefix.Length..];

        return name;
    }

    /// <summary>筛选值归一：空与「全部」都是「不筛选」。</summary>
    private static string? Selected(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 || trimmed == All ? null : trimmed;
    }

    /// <summary>
    /// 域的候选（REQ-UI-059）。首项固定为「全部」：
    /// 选择框没有「清空」这个动作，不给一个「全部」的话筛了就退不回来。
    /// </summary>
    private static async Task<CommandResult> DomainsAsync(Sources sources)
    {
        if (sources.Catalog?.Invoke() is not { } catalog)
            return NoCatalog();

        await catalog.RefreshAsync().ConfigureAwait(true);
        return Options(catalog.Domains);
    }

    /// <summary>
    /// 类的候选，取值范围随域收敛（DEC-021 严格两级）。
    ///
    /// 域为「全部」时只给「全部」一项：跨域列类名会把不同域里同名的类
    /// 归成一条，筛出来的结果没有人能解释。
    /// </summary>
    private static async Task<CommandResult> ClassesAsync(Sources sources, string? domain)
    {
        if (sources.Catalog?.Invoke() is not { } catalog)
            return NoCatalog();

        await catalog.RefreshAsync().ConfigureAwait(true);

        if (Selected(domain) is not { } scope)
            return Options([]);

        var classes = catalog.Entries
            .Where(entry => (entry.Domain ?? "").Equals(scope, StringComparison.OrdinalIgnoreCase))
            .Select(entry => CommandClassLabels.Display(entry.CommandClass))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value == CommandClassLabels.None ? 1 : 0)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();

        return Options(classes);
    }

    /// <summary>候选项行集：列名固定 <c>value</c>，与选择通道发布的列名同一个字。</summary>
    private static CommandResult Options(IEnumerable<string> values)
        => Rows(new[] { All }
            .Concat(values ?? [])
            .Select(value => new Dictionary<string, string> { ["value"] = value })
            .ToList());

    /// <summary>
    /// 三个筛选条件求交：域、类、搜索词。
    ///
    /// 域与类的两级联动下拉 1.8.18 随描述式改造去掉过一版——理由是「联动下拉的候选是
    /// 动态的，而控制面板的选项框只收静态候选」。1.9.2 补上了动态候选
    /// （<c>optionsSource</c>，REQ-UI-059），这条限制不再成立，两级下拉因此回来了。
    ///
    /// 搜索词仍然横跨指令名、说明与域：它是「模糊找一条」，与「按域收窄范围」不是一件事，
    /// 两者叠加而不是互相取代。
    /// </summary>
    private static bool Matches(CatalogEntry entry, string needle, string? domain, string? commandClass)
    {
        if (domain != null && !(entry.Domain ?? "").Equals(domain, StringComparison.OrdinalIgnoreCase))
            return false;

        // 类按**显示标签**比对：下拉里选的是「无类」，而描述符里那一格是空串。
        if (commandClass != null
            && !CommandClassLabels.Display(entry.CommandClass)
                .Equals(commandClass, StringComparison.OrdinalIgnoreCase))
            return false;

        if (needle.Length == 0)
            return true;

        return entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (entry.Domain ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CommandResult> DetailAsync(Sources sources, string? name)
    {
        if (sources.Catalog?.Invoke() is not { } catalog)
            return NoCatalog();
        if (string.IsNullOrWhiteSpace(name))
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
        if (sources.Catalog?.Invoke() is not { } catalog)
            return NoCatalog();
        if (string.IsNullOrWhiteSpace(name))
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
                ["commands"] = DomainCommandCount(bus.Registry, module)
                    .ToString(CultureInfo.InvariantCulture),
                ["description"] = module.Description,
            })
            .ToList();

        return Rows(rows);
    }

    /// <summary>
    /// 按域统计当前注册表里的指令条数；域名取自模块名（History 前缀剥离）。
    ///
    /// 这是**该域当前注册的指令总数**，不是本模块经模块路径注册的条数。Aurora 的
    /// 自持命令仍从界面注册表统计；宿主模块命令不在这张表里时，回退到模块清单在
    /// 注册完成后定稿的 CommandCount，避免把“不可见”误显示成 0。
    /// </summary>
    internal static int DomainCommandCount(CommandRegistry registry, HistoryVulcan.Services.Modules.ModuleMeta module)
    {
        var domain = ModuleDomainNaming.ToDomain(module.ModuleName);
        if (domain.Length == 0)
            return 0;

        var localCount = registry.All().Count(descriptor =>
            string.Equals(DomainOf(descriptor), domain, StringComparison.OrdinalIgnoreCase));

        // 在进程内 UI 模式下，宿主模块指令不在 Aurora 自己的注册表里。
        // 本地计数为 0 不是模块没有指令，而是当前总线的可见范围不同；模块清单
        // 的 CommandCount 是宿主完成注册后的权威快照，作为远端模块的兜底值。
        return localCount > 0 ? localCount : module.CommandCount;
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
        registry.Register(descriptor, FrontendCommandSource.Name);
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value;
}
