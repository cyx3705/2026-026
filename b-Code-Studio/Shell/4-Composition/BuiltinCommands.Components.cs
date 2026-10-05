using HistoryAurora.Shell.Components.Catalog;
using HistoryAurora.Shell.Components.Themes;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Neutral.Commands;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 组件目录指令：页面协议的说明书就是 <see cref="ComponentCatalog"/>，这两条把它交给模块作者与 AI。
///
/// **对远端开放**：写页面描述的往往是另一个仓里的 AI，它只够得着 MCP。
/// 两条都是纯读，无条件登记——它们必须在 Attach 那一刻进宿主注册表。
/// </summary>
internal static partial class BuiltinCommands
{
    private static void RegisterComponents(CommandTable r)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.component.list",
            Domain = "aurora",
            CommandClass = "component",
            Summary = "列出页面协议的全部组件与协议：页面节点、面板小组件、页面信封、动作声明、选择通道、取数载荷、XAML 令牌，各一句用途",
            Example = "aurora.component.list",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                ComponentCatalog.DescribeAll(),
                ComponentCatalog.All.Select(spec => new { spec.Name, spec.Layer, spec.Summary }).ToList())),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.component.show",
            Domain = "aurora",
            CommandClass = "component",
            Summary = "看一个组件或协议的完整用法：字段（类型、必填、缺省、取值）、规矩与照抄能跑的示例；xaml 一条附带运行中现有的全部 Aurora.* 键",
            Example = "aurora.component.show name=table",
            Readonly = true,
            RequiresUiThread = true,
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
                if (spec == null)
                    return CommandResult.Fail($"没有名为 {name} 的组件或协议；aurora.component.list 可查全部");

                var keys = spec.Name == ComponentCatalog.XamlSpecName ? AuroraComponentResources.PublicKeys() : null;
                return CommandResult.Ok(ComponentCatalog.Describe(spec, keys), spec);
            }),
        });
    }
}
