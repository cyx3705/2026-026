namespace HistoryAurora.Shell.HostedPages.Views;

/// <summary>
/// Aurora 自持页面的描述（REQ-UI-052）。**界面用自己的协议描述自己的页面。**
///
/// 1.8.18 之前这里是分裂的：组件测试页确实是描述式的，而命令集、指令详情、模块管理
/// 三页是手搓 WPF——于是「模块不得自建组件」这条禁令，界面自己是全仓最大的违反者。
/// 更实际的后果是：组件层缺什么，只有模块作者会撞上，而他们没有权限补；
/// 界面自己不用这套协议，就永远不会先撞到墙。
///
/// 现在改成自己消费自己：这几页与 Mercury、Janus 的页走**同一个解析器、同一个渲染器、
/// 同一个注册器**（<see cref="HistoryAurora.Shell.Components.Pages.PageRegistrar"/>），
/// 唯一的区别是描述从这个常量来，而不是从 <c>&lt;域&gt;.ui.describe</c> 回来。
///
/// 因此这里表达不出来的东西，就是组件层真正缺的东西——而且是界面自己先疼。
/// 1.9.2 的域/类联动下拉就是一次兑现：这一页要它，协议表达不了，
/// 于是补的是组件能力（<c>optionsSource</c>，REQ-UI-059），不是给这一页开个后门。
/// </summary>
internal static class HostedPageDescriptions
{
    /// <summary>自持页面的 owner。就是界面自己，不借用一个不存在的模块名。</summary>
    public const string Owner = "HistoryAurora";

    public const string Json = """
        {
          "schemaVersion": 1,
          "owner": "HistoryAurora",
          "pages": [
            {
              "id": "mcp",
              "title": "命令集",
              "scene": "HistoryAurora",
              "placement": { "side": "center", "ratio": 0.5, "visible": true, "singleton": true },
              "content": {
                "type": "stack",
                "gap": "tight",
                "children": [
                  {
                    "type": "panel",
                    "id": "mcp-filter",
                    "rows": [
                      {
                        "mode": "flex",
                        "widgets": [
                          { "kind": "textbox", "id": "domain", "label": "域", "mode": "select",
                            "minWidth": 96, "channel": "aurora.mcp.domain",
                            "optionsSource": {
                              "command": "aurora.ui.data",
                              "args": { "view": "domains" }
                            } },
                          { "kind": "textbox", "id": "class", "label": "类", "mode": "select",
                            "minWidth": 84, "channel": "aurora.mcp.class",
                            "optionsSource": {
                              "command": "aurora.ui.data",
                              "args": { "view": "classes",
                                        "domain": "{selection.aurora.mcp.domain.value}" }
                            } },
                          { "kind": "textbox", "id": "query", "label": "搜索", "flex": true,
                            "channel": "aurora.mcp.query" }
                        ]
                      }
                    ]
                  },
                  {
                    "type": "table",
                    "id": "mcp-commands",
                    "channel": "aurora.mcp.command",
                    "dataSource": {
                      "command": "aurora.ui.data",
                      "args": { "view": "commands",
                                "query": "{selection.aurora.mcp.query.value}",
                                "domain": "{selection.aurora.mcp.domain.value}",
                                "class": "{selection.aurora.mcp.class.value}" }
                    },
                    "columns": [
                      { "key": "domain", "title": "域", "width": "90" },
                      { "key": "class", "title": "类", "width": "76" },
                      { "key": "method", "title": "方法", "width": "150" },
                      { "key": "readonly", "title": "只读", "width": "48" },
                      { "key": "summary", "title": "说明", "width": "*" }
                    ],
                    "rowActions": [
                      { "action": "mcp.detail", "title": "详情", "inline": false },
                      { "action": "mcp.prefill", "title": "填入", "inline": false },
                      { "action": "mcp.copyexample", "title": "复制示例", "inline": false },
                      { "action": "mcp.run", "title": "运行（仅只读指令）", "inline": false }
                    ]
                  }
                ]
              }
            },
            {
              "id": "commanddetail",
              "title": "指令详情",
              "scene": "HistoryAurora",
              "placement": { "side": "right", "ratio": 0.28, "visible": true, "singleton": true },
              "content": {
                "type": "stack",
                "gap": "tight",
                "children": [
                  {
                    "type": "panel",
                    "id": "detail-actions",
                    "rows": [
                      {
                        "mode": "even",
                        "widgets": [
                          { "kind": "button", "action": "detail.prefill", "text": "填入控制台",
                            "enabledWhen": { "selected": "aurora.mcp.command" } },
                          { "kind": "button", "action": "detail.copyexample", "text": "复制示例",
                            "enabledWhen": { "selected": "aurora.mcp.command" } },
                          { "kind": "button", "action": "detail.run", "text": "运行（仅只读）",
                            "enabledWhen": { "selected": "aurora.mcp.command" } }
                        ]
                      }
                    ]
                  },
                  {
                    "type": "table",
                    "id": "detail-facts",
                    "dataSource": {
                      "command": "aurora.ui.data",
                      "args": { "view": "commanddetail",
                                "name": "{selection.aurora.mcp.command.name}" }
                    },
                    "columns": [
                      { "key": "key", "title": "项", "width": "72" },
                      { "key": "value", "title": "值", "width": "*" }
                    ]
                  },
                  {
                    "type": "table",
                    "id": "detail-params",
                    "dataSource": {
                      "command": "aurora.ui.data",
                      "args": { "view": "commandparams",
                                "name": "{selection.aurora.mcp.command.name}" }
                    },
                    "columns": [
                      { "key": "name", "title": "参数", "width": "110" },
                      { "key": "type", "title": "类型", "width": "60" },
                      { "key": "required", "title": "必填", "width": "44" },
                      { "key": "summary", "title": "说明", "width": "*" }
                    ]
                  }
                ]
              }
            },
            {
              "id": "modules",
              "title": "模块管理",
              "scene": "HistoryAurora",
              "placement": { "side": "right", "ratio": 0.32, "visible": true, "singleton": true },
              "content": {
                "type": "stack",
                "gap": "tight",
                "children": [
                  {
                    "type": "panel",
                    "id": "modules-actions",
                    "rows": [
                      {
                        "mode": "even",
                        "widgets": [
                          { "kind": "button", "action": "modules.reload", "text": "刷新模块" },
                          { "kind": "button", "action": "modules.hotreload", "text": "热重载" },
                          { "kind": "button", "action": "modules.opendir", "text": "打开发现根" }
                        ]
                      }
                    ]
                  },
                  {
                    "type": "table",
                    "id": "modules-list",
                    "channel": "aurora.modules.module",
                    "dataSource": { "command": "aurora.ui.data", "args": { "view": "modules" } },
                    "columns": [
                      { "key": "module", "title": "模块", "width": "132" },
                      { "key": "version", "title": "版本", "width": "62" },
                      { "key": "commands", "title": "域指令", "width": "56" },
                      { "key": "description", "title": "描述", "width": "*" }
                    ]
                  }
                ]
              }
            },
            {
              "id": "components",
              "title": "组件测试",
              "scene": "HistoryAurora",
              "placement": { "side": "center", "visible": true, "singleton": true },
              "content": {
                "type": "stack",
                "gap": "normal",
                "children": [
                  { "type": "text", "text": "Aurora 组件测试页" },
                  { "type": "text", "style": "secondary", "text": "描述化页面、表格、面板和基础控件的统一视觉验收。" },
                  { "type": "text", "style": "caption", "text": "浅色圆角只出现在控制面板；表格直接平铺。列可以拖着换顺序，占满剩余宽度的那一列除外。" },
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
                        ]
                      },
                      {
                        "type": "panel",
                        "id": "demo-panel",
                        "text": "控制面板",
                        "rows": [
                          {
                            "widgets": [
                              { "kind": "text", "text": "第一行：可变宽度，最右边那个元素吃掉余量。" }
                            ]
                          },
                          {
                            "widgets": [
                              { "kind": "textbox", "id": "picked", "label": "选中项",
                                "follows": "aurora.preview.item.name" },
                              { "kind": "button", "action": "preview.rename", "text": "跟随选中",
                                "enabledWhen": { "selected": "aurora.preview.item" } }
                            ]
                          },
                          {
                            "widgets": [
                              { "kind": "textbox", "id": "note", "label": "说明", "value": "组件演示",
                                "flex": true },
                              { "kind": "button", "action": "preview.apply", "text": "应用" }
                            ]
                          },
                          {
                            "mode": "even",
                            "widgets": [
                              { "kind": "button", "action": "preview.apply", "text": "普通按钮" },
                              { "kind": "button", "action": "preview.apply", "text": "强调按钮" },
                              { "kind": "button", "action": "preview.apply", "text": "危险按钮" }
                            ]
                          },
                          {
                            "widgets": [
                              { "kind": "textbox", "id": "option", "label": "选项", "mode": "select",
                                "options": [ "浅色", "深色", "跟随系统" ], "value": "浅色" },
                              { "kind": "textbox", "id": "page-option", "label": "页面选项", "mode": "select",
                                "channel": "aurora.preview.section",
                                "options": [ "第一项", "第二项", "第三项" ] }
                            ]
                          }
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
                        "id": "demo-switch-table",
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
                        "rows": [
                          {
                            "widgets": [
                              { "kind": "text", "text": "第三支：换掉的是组件，不是页面。" },
                              { "kind": "button", "action": "preview.apply", "text": "分支内按钮" }
                            ]
                          }
                        ]
                      }
                    ]
                  },
                  { "type": "text", "style": "secondary", "text": "弹出层内容继续复用控制面板控件" },
                  {
                    "type": "popup",
                    "id": "demo-popup",
                    "text": "打开弹出层",
                    "rows": [
                      {
                        "widgets": [
                          { "kind": "text", "text": "低频编辑内容放在这里。" }
                        ]
                      },
                      {
                        "widgets": [
                          { "kind": "textbox", "id": "popup-option", "label": "模式", "mode": "select",
                            "options": [ "全部", "仅主线", "仅分支" ] }
                        ]
                      },
                      {
                        "mode": "even",
                        "widgets": [
                          { "kind": "button", "action": "preview.apply", "text": "保存" }
                        ]
                      }
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
