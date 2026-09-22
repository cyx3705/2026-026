using HistoryAurora.Shell.Components.Scenes;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 场景指令组（REQ-UI-085，见 b-Office/history/场景与导航方案V1.0.md）。
///
/// **无条件登记**：它们必须在 Attach 那一刻进宿主注册表（晚登记的界面命令宿主永远看不见，
/// 见 <see cref="Register"/> 的注释）。目录快照那一路 <c>Scenes</c> 为 null，但处理器从不执行。
///
/// 除 <c>aurora.nav.open</c> 外都对模型开放：「切到 Minerva」是一次 MCP 调用（用户拍板）。
/// </summary>
internal static partial class BuiltinCommands
{
    private static void RegisterScenes(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.scene.list",
            Domain = "aurora",
            CommandClass = "scene",
            Summary = "列出全部场景（按使用频次排序）：当前场景、来源、使用次数。场景不含页面集合，只是一份布局",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => WithScenes(s, scenes =>
            {
                var list = scenes.List();
                var lines = list.Select(scene =>
                    $"\n  {(scene.Active ? "*" : " ")} {scene.Title} [{scene.Id}] {SourceText(scene.Source)}"
                    + (scene.Uses > 0 ? $" · 用过 {scene.Uses} 次" : ""));
                var rows = list.Select(scene => new
                {
                    id = scene.Id,
                    title = scene.Title,
                    source = SourceText(scene.Source),
                    active = scene.Active,
                    uses = scene.Uses,
                }).ToList();
                return CommandResult.Ok($"共 {list.Count} 个场景（* 为当前）:" + string.Concat(lines), rows);
            })),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.scene.go",
            Domain = "aurora",
            CommandClass = "scene",
            Summary = "切到一个场景：恢复它上次的布局与显隐；模块场景第一次进入时露出该模块的页与常驻页（控制台、命令集）",
            Example = "aurora.scene.go id=HistoryMinerva",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "id",
                    Description = "场景 id 或标题（aurora.scene.list 可查）；模块场景的 id 就是模块名，也认去掉 History 的简称",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                WithScenes(s, scenes => FromScene(scenes.Go(ctx.RequireString("id"))))),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.scene.open",
            Domain = "aurora",
            CommandClass = "scene",
            Summary = "在当前场景里打开一页（不切场景）；它回到自己的位置，那个位置原来的页被隐藏（一格一页）",
            Example = "aurora.scene.open page=graph",
            RequiresUiThread = true,
            Parameters = [PageParameter()],
            Handler = CommandDescriptor.Sync(ctx =>
                WithScenes(s, scenes => FromScene(scenes.Open(ctx.RequireString("page"))))),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.scene.save",
            Domain = "aurora",
            CommandClass = "scene",
            Summary = "把当前露面的页面与布局另存为一个场景，并切到它",
            Example = "aurora.scene.save id=出图 title=出图与提交",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "id",
                    Description = "场景 id，同时是命名布局的文件名；不能与模块场景或「all」重名",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec { Name = "title", Description = "显示名，省略时同 id", Position = 1 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                WithScenes(s, scenes => FromScene(scenes.Save(ctx.RequireString("id"), ctx.GetString("title"))))),
        });

        // aurora.scene.add / remove 在 1.20.0 退役（REQ-UI-094）：场景不拥有页面，显隐就是 aurora.ui.show / hide。

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.scene.reset",
            Domain = "aurora",
            CommandClass = "scene",
            Summary = "场景回到默认形态：布局按各页声明重建，露面的页回到初值（省略 scene 为当前场景；另存场景只能在当前时重排）",
            RequiresUiThread = true,
            Parameters = [SceneParameter(position: 0)],
            Handler = CommandDescriptor.Sync(ctx =>
                WithScenes(s, scenes => FromScene(scenes.Reset(ctx.GetString("scene"))))),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.scene.delete",
            Domain = "aurora",
            CommandClass = "scene",
            Summary = "删除一个另存的场景；模块场景随模块装卸，不能删",
            Example = "aurora.scene.delete id=出图",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "另存场景的 id", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
                WithScenes(s, scenes => FromScene(scenes.Delete(ctx.RequireString("id"))))),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.nav.open",
            HiddenReason = "把光标放进界面右栏的搜索框，只对坐在屏幕前的人有意义",
            Domain = "aurora",
            CommandClass = "nav",
            Summary = "聚焦右栏搜索框：搜索并打开场景与页面。全局快捷键由 HistoryMercury 注册",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Window.FocusNavigatorSearch();
                return CommandResult.Ok("已聚焦右栏搜索框");
            }),
        });
    }

    private static ParameterSpec PageParameter() => new()
    {
        Name = "page",
        Description = "页面 id 或标题（aurora.ui.windows 可查）",
        Required = true,
        Position = 0,
    };

    private static ParameterSpec SceneParameter(int position) => new()
    {
        Name = "scene",
        Description = "场景 id 或标题；省略为当前场景",
        Position = position,
    };

    private static CommandResult WithScenes(ShellCommandServices s, Func<SceneManager, CommandResult> action)
        => s.Scenes == null ? CommandResult.Fail("场景未启用") : action(s.Scenes);

    private static CommandResult FromScene(SceneResult result)
        => result.Ok ? CommandResult.Ok(result.Message) : CommandResult.Fail(result.Message);

    private static string SourceText(SceneSource source) => source switch
    {
        SceneSource.All => "内置",
        SceneSource.Derived => "模块",
        _ => "另存",
    };
}
