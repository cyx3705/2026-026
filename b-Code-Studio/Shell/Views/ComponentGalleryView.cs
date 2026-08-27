using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.CommandSurface;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Views;

internal sealed class ComponentGalleryView : UserControl
{
    public ComponentGalleryView(
        CommandBus bus,
        IShellLog log,
        ActionRegistry actions,
        AuroraCompletionProvider completions,
        SelectionChannels? channels = null,
        PageDataRefresher? refresher = null)
    {
        var parsed = PageDescriptionReader.Read(ComponentGalleryDescription.Json, ComponentGalleryCommands.Owner);
        if (!parsed.Ok)
            throw new InvalidOperationException("组件测试页描述无效: " + parsed.Error);

        var rendered = PageRenderer.Render(
            parsed.Value!.Pages[0],
            new PageRenderContext
            {
                Bus = bus,
                Log = log,
                Owner = ComponentGalleryCommands.Owner,
                Actions = actions,
                Completions = completions,
                Channels = channels,
                Refresher = refresher,
            });

        if (rendered.MissingComponents.Count > 0)
            throw new InvalidOperationException(
                "组件测试页存在缺件: " + string.Join(", ", rendered.MissingComponents));

        var root = rendered.Root;
        root.Margin = new Thickness(12);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
    }
}

internal static class ComponentGalleryDescription
{
    public const string Json = """
        {
          "schemaVersion": 1,
          "owner": "HistoryAurora",
          "pages": [
            {
              "id": "components",
              "title": "组件测试",
              "placement": { "side": "center", "visible": true, "singleton": true },
              "content": {
                "type": "stack",
                "gap": "normal",
                "children": [
                  { "type": "text", "text": "Aurora 组件测试页" },
                  { "type": "text", "style": "secondary", "text": "描述化页面、表格、面板和基础控件的统一视觉验收。" },
                  { "type": "text", "style": "caption", "text": "浅色圆角只出现在控制面板；表格直接平铺。" },
                  {
                    "type": "grid",
                    "min": 420,
                    "gap": "normal",
                    "children": [
                      {
                        "type": "table",
                        "id": "demo-table",
                        "channel": "aurora.preview.item",
                        "dataSource": { "command": "aurora.preview.rows" },
                        "columns": [
                          { "key": "name", "title": "名称", "width": "150" },
                          { "key": "kind", "title": "类别", "width": "70" },
                          { "key": "state", "title": "状态", "width": "76" },
                          { "key": "subject", "title": "最近提交", "width": "*" }
                        ],
                        "rowActions": [
                          { "action": "preview.row", "title": "查看行" }
                        ],
                        "view": { "filterable": true, "sortable": true, "selection": "single" }
                      },
                      {
                        "type": "panel",
                         "id": "demo-panel",
                         "text": "控制面板",
                         "orientation": "horizontal",
                         "widgets": [
                           { "kind": "text", "text": "面板内控件同级排列，由渐隐线分割。" },
                           { "kind": "textbox", "id": "picked", "label": "选中项", "follows": "aurora.preview.item.name" },
                           { "kind": "button", "action": "preview.rename", "text": "跟随选中", "enabledWhen": { "selected": "aurora.preview.item" } },
                           { "kind": "textbox", "id": "note", "label": "说明", "value": "组件演示", "required": true },
                           { "kind": "textbox", "id": "option", "label": "选项", "mode": "select", "options": [ "浅色", "深色", "跟随系统" ], "value": "浅色" },
                           { "kind": "button", "action": "preview.apply", "text": "普通按钮" },
                           { "kind": "button", "action": "preview.apply", "text": "强调按钮" },
                           { "kind": "button", "action": "preview.apply", "text": "危险按钮" },
                           { "kind": "textbox", "id": "page-option", "label": "页面选项", "mode": "select", "channel": "aurora.preview.section", "options": [ "第一项", "第二项", "第三项" ] }
                         ]
                      }
                    ]
                  },
                  { "type": "text", "style": "secondary", "text": "「页面选项」发布到通道，下面这块跟着换一批组件" },
                  {
                    "type": "switch",
                    "id": "demo-switch",
                    "source": "{selection.aurora.preview.section.value}",
                    "children": [
                      {
                        "type": "stack",
                        "case": "第一项",
                        "gap": "tight",
                        "children": [
                          { "type": "text", "style": "caption", "text": "第一支：只有一段文字。" }
                        ]
                      },
                      {
                        "type": "table",
                        "case": "第二项",
                        "dataSource": { "command": "aurora.preview.rows" },
                        "columns": [
                          { "key": "name", "title": "名称", "width": "150" },
                          { "key": "subject", "title": "最近提交", "width": "*" }
                        ]
                      },
                      {
                        "type": "panel",
                        "case": "第三项",
                        "id": "demo-switch-panel",
                        "text": "第三支",
                        "orientation": "horizontal",
                        "widgets": [
                          { "kind": "text", "text": "第三支：换掉的是组件，不是页面。" },
                          { "kind": "button", "action": "preview.apply", "text": "分支内按钮" }
                        ]
                      }
                    ]
                  },
                  { "type": "text", "style": "secondary", "text": "弹出层内容继续复用控制面板控件" },
                  {
                        "type": "popup",
                        "id": "demo-popup",
                        "text": "打开弹出层",
                        "widgets": [
                          { "kind": "text", "text": "低频编辑内容放在这里。" },
                          { "kind": "textbox", "id": "popup-option", "label": "模式", "mode": "select", "options": [ "全部", "仅主线", "仅分支" ] },
                          { "kind": "button", "action": "preview.apply", "text": "保存" }
                        ]
                  },
                  { "type": "text", "style": "secondary", "text": "响应式栅格与泳道图" },
                  {
                    "type": "swimlane",
                    "id": "demo-graph",
                    "dataSource": { "command": "aurora.preview.graph" }
                  }
                ]
              }
            }
          ]
        }
        """;
}
