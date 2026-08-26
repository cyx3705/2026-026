using HistoryAurora.Shell.Actions;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Views;

/// <summary>
/// 组件测试页自带的取数指令与动作声明。
///
/// **为什么不放进 <see cref="BuiltinCommands"/>**：那是启动时一次性装配的全局指令组，
/// 页面能不能取到数就变成了「注册顺序对不对」的问题——顺序一旦被别的改动挪动，
/// 症状是页面上一条 `✗ 未知指令: aurora.preview.graph`，而代码里那条指令明明写着。
/// 改成由页面自己在打开前登记：指令存在与页面存在是同一件事，顺序问题不复存在。
///
/// **为什么动作走 <see cref="ActionRegistry.DeclareLocal"/> 而不是 <c>&lt;域&gt;.ui.actions</c>**：
/// 拉取协议按命令名反推模块域，`aurora.preview.ui.actions` 会被反推成域 `aurora.preview`、
/// owner `HistoryAurora.preview`，与声明里的 owner 对不上，整份声明被跳过——
/// 页面上每个按钮都变成「未声明的动作」。界面自带的页面不该把自己伪装成模块。
/// </summary>
internal static class ComponentGalleryCommands
{
    /// <summary>本页的 owner。就是界面自己，不再借用一个不存在的模块名。</summary>
    public const string Owner = "HistoryAurora";

    /// <summary>登记取数指令。幂等：已经在注册表里的不再登记第二遍。</summary>
    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Add(registry, new CommandDescriptor
        {
            Name = "aurora.preview.echo",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "preview",
            Summary = "记录组件测试页动作",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok("组件测试动作已触发")),
        });

        Add(registry, new CommandDescriptor
        {
            Name = "aurora.preview.rows",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "preview",
            Summary = "组件测试页表格数据",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                [
                  { "name": "alpha", "kind": "模块", "state": "已加载", "subject": "短文本" },
                  { "name": "beta", "kind": "模块", "state": "有修改", "subject": "这是一段足够长的最近提交说明，用来观察表格的星号列、截断提示和窗口缩放行为。" },
                  { "name": "gamma", "kind": "工具", "state": "未知", "subject": "自适应列与固定列同时存在" },
                  { "name": "delta", "kind": "服务", "state": "已加载", "subject": "第四条演示数据" }
                ]
                """)),
        });

        Add(registry, new CommandDescriptor
        {
            Name = "aurora.preview.graph",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "preview",
            Summary = "组件测试页泳道数据",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                {
                  "schemaVersion": 1,
                  "title": "组件测试图谱 · 3 条泳道 · 6 个节点",
                  "lanes": [
                    { "id": "main", "title": "主线", "tip": "m3", "open": true },
                    { "id": "feature", "title": "演示分支", "tip": "f2", "open": true },
                    { "id": "archive", "title": "归档", "tip": "a1", "open": false }
                  ],
                  "nodes": [
                    { "id": "m1", "title": "m1", "subtitle": "初始节点", "parents": [] },
                    { "id": "f1", "title": "f1", "subtitle": "分支节点", "parents": [ "m1" ], "lane": "feature" },
                    { "id": "f2", "title": "f2", "subtitle": "当前分支", "parents": [ "f1" ], "lane": "feature", "tone": "accent" },
                    { "id": "a1", "title": "a1", "subtitle": "归档节点", "parents": [ "m1" ], "lane": "archive" },
                    { "id": "m2", "title": "m2", "subtitle": "合并节点", "parents": [ "m1", "f2" ] },
                    { "id": "m3", "title": "m3", "subtitle": "当前主线", "parents": [ "m2", "a1" ], "tone": "accent" }
                  ],
                  "selectAction": "preview.node"
                }
                """)),
        });
    }

    /// <summary>本页按钮、行操作与泳道节点绑定的动作。</summary>
    public static IReadOnlyList<ActionDeclaration> Actions =>
    [
        new ActionDeclaration
        {
            Id = "preview.apply",
            Title = "应用演示",
            Command = "aurora.preview.echo",
            Args = new Dictionary<string, string> { ["note"] = "{note}", ["option"] = "{option}" },
            Summary = "记录当前演示控件的值",
        },
        new ActionDeclaration
        {
            Id = "preview.row",
            Title = "查看行",
            Command = "aurora.preview.echo",
            Args = new Dictionary<string, string> { ["row"] = "{name}" },
            Summary = "记录当前表格行",
        },
        new ActionDeclaration
        {
            Id = "preview.node",
            Title = "查看节点",
            Command = "aurora.preview.echo",
            Args = new Dictionary<string, string> { ["node"] = "{node}" },
            Summary = "记录当前泳道节点",
        },
    ];

    private static void Add(CommandRegistry registry, CommandDescriptor descriptor)
    {
        if (registry.TryGet(descriptor.Name, out _))
            return;
        registry.Register(descriptor, FrontendCommandCatalog.Source);
    }
}
