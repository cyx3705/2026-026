using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell;

internal static partial class BuiltinCommands
{
    private static void RegisterComponentGallery(CommandRegistry registry)
    {
        RegisterFrontend(registry, new CommandDescriptor
        {
            Name = "aurora.preview.ui.actions",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "组件测试页动作声明",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                {
                  "schemaVersion": 1,
                  "owner": "HistoryPreview",
                  "actions": [
                    {
                      "id": "preview.apply",
                      "title": "应用演示",
                      "command": "aurora.preview.echo",
                      "args": { "note": "{note}", "option": "{option}" },
                      "summary": "记录当前演示控件的值"
                    },
                    {
                      "id": "preview.row",
                      "title": "查看行",
                      "command": "aurora.preview.echo",
                      "args": { "row": "{name}" },
                      "summary": "记录当前表格行"
                    },
                    {
                      "id": "preview.node",
                      "title": "查看节点",
                      "command": "aurora.preview.echo",
                      "args": { "node": "{node}" },
                      "summary": "记录当前泳道节点"
                    }
                  ]
                }
                """)),
        });

        RegisterFrontend(registry, new CommandDescriptor
        {
            Name = "aurora.preview.echo",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "demo",
            Summary = "记录组件测试页动作",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok("组件测试动作已触发")),
        });

        RegisterFrontend(registry, new CommandDescriptor
        {
            Name = "aurora.preview.rows",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "demo",
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

        RegisterFrontend(registry, new CommandDescriptor
        {
            Name = "aurora.preview.graph",
            HiddenReason = "组件测试页内部指令",
            Domain = "aurora",
            CommandClass = "demo",
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
}
