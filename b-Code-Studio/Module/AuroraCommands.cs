using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HistoryAurora.Shell.Components.Catalog;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.HostedPages.Views;
using HistoryAurora.Shell.Neutral;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Module;

/// <summary>
/// Aurora 的 aurora.* 指令（2.1.0）：界面操作转给网页执行，取数与台账留在模块里。
/// </summary>
/// <remarks>
/// <para>
/// <b>转给网页的</b>写在随程序集内嵌的 <c>AuroraCommands.json</c>：那里就是它们的自描述（名称、摘要、示例、参数），
/// 执行时原样把参数推给最近活跃的网页（<see cref="WebProcess.InvokeAsync"/>），网页里的同名实现给出回执。
/// 加一条界面指令 = 网页 <c>browser-commands.js</c> 实现它 + 这份清单加一条。
/// </para>
/// <para>
/// <b>留在模块里的</b>不需要界面：自持页面取数（aurora.ui.data）、控制台日志快照、组件目录、组件测试页取数、
/// 组件申请台账、模块管理的组合指令、打开数据目录、宿主就绪回调。网页与 MCP 调到的是同一份实现。
/// </para>
/// </remarks>
internal static class AuroraCommands
{
    internal const string ManifestResource = "HistoryAurora.Module.AuroraCommands.json";

    /// <summary>登记全部 aurora.* 指令到本模块的指令表。</summary>
    public static void Register(CommandTable table, Services services)
    {
        RegisterForwarded(table, services.Web);
        RegisterLogSnapshot(table, services.Log);
        RegisterComponents(table);
        RegisterRequests(table, services.Requests);
        RegisterApp(table, services);
        RegisterHostedActions(table);
        ComponentGalleryCommands.Register(table);
        HostedPageData.Register(table, new HostedPageData.Sources
        {
            Bus = () => services.Bus,
            Catalog = () => services.Catalog,
        });
    }

    /// <summary>指令处理要用到的服务。</summary>
    internal sealed class Services
    {
        public required WebProcess Web { get; init; }

        public required MemoryShellLog Log { get; init; }

        public required ShellBus Bus { get; init; }

        public required LocalCommandCatalogSession Catalog { get; init; }

        public required ComponentRequestStore Requests { get; init; }

        public required string DataDirectory { get; init; }
    }

    // ---------------------------------------------------------------- 转给网页

    /// <summary>读内嵌清单；清单损坏是构建问题，直接抛。</summary>
    internal static IReadOnlyList<CommandDescriptor> ReadForwarded(Func<string, Func<CommandContext, Task<CommandResult>>> handler)
    {
        using var stream = typeof(AuroraCommands).Assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException($"缺少内嵌指令清单 {ManifestResource}");
        using var document = JsonDocument.Parse(stream);
        var list = new List<CommandDescriptor>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var parts = name.Split('.');
            var descriptor = new CommandDescriptor
            {
                Name = name,
                Domain = parts[0],
                CommandClass = parts[1],
                Summary = item.GetProperty("summary").GetString()!,
                Example = String(item, "example"),
                Readonly = Bool(item, "readonly"),
                HiddenReason = String(item, "hiddenReason"),
                AllowUnspecifiedParameters = Bool(item, "allowUnspecifiedParameters"),
                Parameters = item.TryGetProperty("parameters", out var parameters)
                    ? [.. parameters.EnumerateArray().Select(ReadParameter)]
                    : [],
                Handler = handler(name),
            };
            list.Add(descriptor);
        }
        return list;
    }

    private static void RegisterForwarded(CommandTable table, WebProcess web)
    {
        foreach (var descriptor in ReadForwarded(name => context => web.InvokeAsync(name, ArgumentsOf(context), context.Cancellation)))
            table.Register(descriptor, FrontendCommandSource.Name);
    }

    private static ParameterSpec ReadParameter(JsonElement item) => new()
    {
        Name = item.GetProperty("name").GetString()!,
        Description = String(item, "description") ?? "",
        Type = String(item, "type") switch
        {
            "int" => ParamType.Int,
            "double" => ParamType.Double,
            "bool" => ParamType.Bool,
            _ => ParamType.String,
        },
        Required = Bool(item, "required"),
        Default = String(item, "default"),
        Position = item.TryGetProperty("position", out var position) && position.TryGetInt32(out var value) ? value : null,
        AllowedValues = item.TryGetProperty("allowedValues", out var allowed)
            ? [.. allowed.EnumerateArray().Select(v => v.GetString()!)]
            : [],
    };

    private static string? String(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 这条指令收到的全部参数（名 → 原文）。
    /// </summary>
    /// <remarks>
    /// 声明过的参数逐个取；允许未声明参数的指令（aurora.ui.invoke 的占位参数等）宿主没有公开读口，
    /// 只能从上下文里那份只读字典取——取不到时退回只交声明过的参数。
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> ArgumentsOf(CommandContext context)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.Descriptor.AllowUnspecifiedParameters && RawArguments(context) is { } raw)
        {
            foreach (var (key, value) in raw)
                args[key] = value;
            return args;
        }
        foreach (var parameter in context.Descriptor.Parameters)
            if (context.Has(parameter.Name) && context.GetString(parameter.Name) is { } value)
                args[parameter.Name] = value;
        return args;
    }

    private static readonly FieldInfo? RawField = typeof(CommandContext)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(field => typeof(IReadOnlyDictionary<string, string>).IsAssignableFrom(field.FieldType));

    private static IReadOnlyDictionary<string, string>? RawArguments(CommandContext context)
        => RawField?.GetValue(context) as IReadOnlyDictionary<string, string>;

    // ---------------------------------------------------------------- 留在模块里的

    private static void RegisterLogSnapshot(CommandTable table, MemoryShellLog log)
        => table.Register(new CommandDescriptor
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
                var minimumLevelText = ctx.GetString("minlevel") ?? "error";
                if (!Enum.TryParse<ShellLogLevel>(minimumLevelText, true, out var minimumLevel)
                    || !Enum.IsDefined(minimumLevel))
                    return CommandResult.Fail($"未知日志级别: {minimumLevelText}");

                var limit = ctx.GetInt("limit", 100);
                if (limit is < 1 or > 500)
                    return CommandResult.Fail("limit 必须在 1~500 之间");

                DateTimeOffset? since = null;
                if (ctx.GetString("since") is { Length: > 0 } sinceText)
                {
                    if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsedSince))
                        return CommandResult.Fail("since 必须是有效的 ISO 8601 时间");
                    since = parsedSince;
                }

                long? after = null;
                if (ctx.GetString("after") is { Length: > 0 } afterText)
                {
                    if (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAfter))
                        return CommandResult.Fail("after 必须是非负整数");
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
        }, FrontendCommandSource.Name);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RegisterComponents(CommandTable table)
    {
        table.Register(new CommandDescriptor
        {
            Name = "aurora.component.list",
            Domain = "aurora",
            CommandClass = "component",
            Summary = "列出页面组件、动作与取数协议",
            Example = "aurora.component.list",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                ComponentCatalog.DescribeAll(),
                ComponentCatalog.All.Select(spec => new { spec.Name, spec.Layer, spec.Summary }).ToList())),
        }, FrontendCommandSource.Name);

        table.Register(new CommandDescriptor
        {
            Name = "aurora.component.show",
            Domain = "aurora",
            CommandClass = "component",
            Summary = "查看页面组件或协议的字段与示例",
            Example = "aurora.component.show name=table",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "组件或协议名，aurora.component.list 可查，例如 table、panel.textbox、page",
                    Required = true,
                    Position = 0,
                    AllowedValues = [.. ComponentCatalog.All.Select(spec => spec.Name)],
                },
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var name = context.GetString("name");
                var spec = ComponentCatalog.Find(name);
                return spec == null
                    ? CommandResult.Fail($"没有名为 {name} 的组件或协议；aurora.component.list 可查全部")
                    : CommandResult.Ok(ComponentCatalog.Describe(spec, null), spec);
            }),
        }, FrontendCommandSource.Name);
    }

    private static void RegisterRequests(CommandTable table, ComponentRequestStore requests)
    {
        table.Register(new CommandDescriptor
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
                if (PageSupport.Supports(component))
                    return CommandResult.Ok($"{component} 已经支持，无需申请");

                var by = context.GetString("by")?.Trim();
                if (string.IsNullOrWhiteSpace(by))
                    by = SourceLabels.Display(context.Source);

                requests.Record(component, by, context.GetString("page"), context.GetString("reason"));
                return CommandResult.Ok($"已登记组件申请: {component}");
            }),
        }, FrontendCommandSource.Name);

        table.Register(new CommandDescriptor
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
        }, FrontendCommandSource.Name);
    }

    /// <summary>Aurora 自持页面引用的全部动作（命令集、模块管理、组件测试）。</summary>
    internal static IReadOnlyList<HistoryAurora.Shell.Components.Actions.ActionDeclaration> HostedActions
        => [.. HostedPageData.Actions, .. ComponentGalleryCommands.Actions];

    /// <summary>
    /// 自持页面的动作声明（2.1.1）。2.1.0 之前这批声明登记在 WPF 窗口的动作台账里，网页经
    /// aurora.ui.actions 读那份台账；台账随 WPF 删除后 aurora.ui.actions 转给网页自己，网页就读不到了，
    /// 原生页面的按钮全部变成「缺少动作」。声明的权威源一直是这两份 C# 清单，这里直接交出去。
    /// </summary>
    private static void RegisterHostedActions(CommandTable table)
        => table.Register(new CommandDescriptor
        {
            Name = "aurora.ui.hostedactions",
            HiddenReason = "界面内部协议不对远程暴露",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "列出前端自带页面的动作声明",
            Example = "aurora.ui.hostedactions",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                $"共 {HostedActions.Count} 条自持页面动作：\n"
                    + string.Join("\n", HostedActions.Select(action => $"{action.Id} → {action.Command}")),
                HostedActions)),
        }, FrontendCommandSource.Name);

    private static void RegisterApp(CommandTable table, Services services)
    {
        table.Register(new CommandDescriptor
        {
            Name = "aurora.app.opendata",
            Domain = "aurora",
            CommandClass = "app",
            Summary = "在系统资源管理器中打开应用数据目录",
            Handler = CommandDescriptor.Sync(_ =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", services.DataDirectory) { UseShellExecute = true })?.Dispose();
                return CommandResult.Ok($"已打开 {services.DataDirectory}");
            }),
        }, FrontendCommandSource.Name);

        table.Register(new CommandDescriptor
        {
            Name = "aurora.host.ready",
            HiddenReason = "宿主装载生命周期回调，不由人或远端触发",
            Domain = "aurora",
            CommandClass = "host",
            Summary = "装载就绪后通知网页重载页面和动作",
            Handler = CommandDescriptor.Sync(_ =>
            {
                services.Web.Broadcast("aurora.ui.reloadpages");
                return CommandResult.Ok(services.Web.ClientCount == 0
                    ? "宿主已就绪；当前没有打开的 Aurora 网页"
                    : $"宿主已就绪，已通知 {services.Web.ClientCount} 个网页重拉页面");
            }),
        }, FrontendCommandSource.Name);
    }
}
